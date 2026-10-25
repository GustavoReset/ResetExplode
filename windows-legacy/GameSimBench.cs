using System.Diagnostics;

namespace ResetExplode;

public sealed record GameSimResult(double AvgFps, double Low1Fps, double MainThreadMs, double ParallelMs, int Frames, double Seconds)
{
    /// <summary>Nota = FPS médio da simulação (frames de jogo por segundo que a CPU aguenta).</summary>
    public double Score => AvgFps;
}

/// <summary>
/// "CPU de jogo": simula o trabalho de CPU de um frame de jogo com 30 mil unidades em dois times.
/// Por frame: (1) thread principal monta a grade espacial (em série, como a maioria das engines);
/// (2) workers em paralelo fazem física, colisão e IA (cada unidade procura inimigos próximos, persegue/foge
/// e troca dano); (3) thread principal roda a lógica de jogo (mortes/renascimento) e prepara a lista de desenho
/// (culling + ordenação por profundidade). Determinístico (semente fixa, estado em buffer duplo).
/// </summary>
public sealed class GameSimBench
{
    public const int Entities = 30_000;
    public const float World = 1000f;
    private const int GridN = 64;                   // 64×64 células
    private const float Cell = World / GridN;
    private const float Radius = Cell;              // raio de percepção
    private const float Dt = 1 / 60f;

    // Estado (SoA) em buffer duplo: lê de "cur", escreve em "nxt".
    private float[] _x = new float[Entities], _y = new float[Entities], _vx = new float[Entities], _vy = new float[Entities];
    private float[] _nx = new float[Entities], _ny = new float[Entities], _nvx = new float[Entities], _nvy = new float[Entities];
    private readonly float[] _hp = new float[Entities];
    private readonly float[] _dmg = new float[Entities];
    private readonly byte[] _team = new byte[Entities];
    private readonly int[] _cellStart = new int[GridN * GridN + 1];
    private readonly int[] _sorted = new int[Entities];
    private readonly int[] _cellOf = new int[Entities];
    private readonly float[] _drawKey = new float[Entities];
    private readonly int[] _drawList = new int[Entities];
    private uint _rng;

    /// <summary>Instantâneo para a interface desenhar (posições e times), atualizado a cada frame.</summary>
    public float[] ViewX => _x;
    public float[] ViewY => _y;
    public byte[] ViewTeam => _team;
    public int FramesDone => Volatile.Read(ref _frames);
    public double Progress { get; private set; }
    public double CurrentFps { get; private set; }
    private int _frames;

    public GameSimResult Run(TimeSpan duration, CancellationToken ct)
    {
        Reset();
        // Aquecimento (JIT, caches, clock da CPU subindo) fora da medição.
        var warm = Stopwatch.StartNew();
        while (warm.Elapsed.TotalSeconds < 2 && !ct.IsCancellationRequested) Step(out _, out _);

        var frameTimes = new List<double>(20000);
        double mainMs = 0, parMs = 0;
        _frames = 0;
        var sw = Stopwatch.StartNew();
        var fpsWindow = Stopwatch.StartNew(); int fpsFrames = 0;
        while (sw.Elapsed < duration && !ct.IsCancellationRequested)
        {
            long t0 = Stopwatch.GetTimestamp();
            Step(out double m, out double p);
            frameTimes.Add(Stopwatch.GetElapsedTime(t0).TotalMilliseconds);
            mainMs += m; parMs += p;
            Interlocked.Increment(ref _frames);
            fpsFrames++;
            if (fpsWindow.ElapsedMilliseconds >= 500) { CurrentFps = fpsFrames / fpsWindow.Elapsed.TotalSeconds; fpsFrames = 0; fpsWindow.Restart(); }
            Progress = sw.Elapsed / duration;
        }

        int n = Math.Max(1, frameTimes.Count);
        var sorted = frameTimes.OrderBy(x => x).ToArray();
        double p99 = sorted.Length > 0 ? sorted[Math.Min(sorted.Length - 1, (int)(sorted.Length * 0.99))] : 0;
        return new GameSimResult(frameTimes.Count / sw.Elapsed.TotalSeconds, p99 > 0 ? 1000 / p99 : 0,
                                 mainMs / n, parMs / n, frameTimes.Count, sw.Elapsed.TotalSeconds);
    }

    private void Reset()
    {
        _rng = 12345;
        for (int i = 0; i < Entities; i++)
        {
            _team[i] = (byte)(i & 1);
            // Times começam em lados opostos e avançam um contra o outro.
            _x[i] = (_team[i] == 0 ? 0.05f : 0.55f) * World + Rnd() * World * 0.4f;
            _y[i] = Rnd() * World;
            _vx[i] = (_team[i] == 0 ? 1 : -1) * (20 + Rnd() * 10);
            _vy[i] = (Rnd() - 0.5f) * 10;
            _hp[i] = 100;
        }
    }

    private void Step(out double mainMs, out double parMs)
    {
        long t0 = Stopwatch.GetTimestamp();
        BuildGrid();                                             // série
        long t1 = Stopwatch.GetTimestamp();
        Parallel.For(0, Environment.ProcessorCount * 4, chunk =>  // paralelo
        {
            int per = (Entities + Environment.ProcessorCount * 4 - 1) / (Environment.ProcessorCount * 4);
            int end = Math.Min(Entities, (chunk + 1) * per);
            for (int i = chunk * per; i < end; i++) Simulate(i);
        });
        long t2 = Stopwatch.GetTimestamp();
        (_x, _nx) = (_nx, _x); (_y, _ny) = (_ny, _y); (_vx, _nvx) = (_nvx, _vx); (_vy, _nvy) = (_nvy, _vy);
        GameLogic();                                             // série
        PrepareDraw();                                           // série
        long t3 = Stopwatch.GetTimestamp();
        mainMs = (t1 - t0 + t3 - t2) * 1000.0 / Stopwatch.Frequency;
        parMs = (t2 - t1) * 1000.0 / Stopwatch.Frequency;
    }

    /// <summary>Counting sort das unidades por célula (grade espacial para buscar vizinhos).</summary>
    private void BuildGrid()
    {
        Array.Clear(_cellStart);
        for (int i = 0; i < Entities; i++)
        {
            int cx = Math.Clamp((int)(_x[i] / Cell), 0, GridN - 1), cy = Math.Clamp((int)(_y[i] / Cell), 0, GridN - 1);
            int c = cy * GridN + cx;
            _cellOf[i] = c;
            _cellStart[c + 1]++;
        }
        for (int c = 0; c < GridN * GridN; c++) _cellStart[c + 1] += _cellStart[c];
        var fill = (int[])_cellStart.Clone();
        for (int i = 0; i < Entities; i++) _sorted[fill[_cellOf[i]]++] = i;
    }

    /// <summary>Física + separação (colisão) + IA de combate de uma unidade. Lê o estado atual, escreve só a própria.</summary>
    private void Simulate(int i)
    {
        float x = _x[i], y = _y[i], vx = _vx[i], vy = _vy[i];
        byte team = _team[i];
        float sepX = 0, sepY = 0, aliX = 0, aliY = 0, cohX = 0, cohY = 0, dmg = 0;
        float bestD2 = Radius * Radius, enemyX = 0, enemyY = 0;
        int allies = 0;
        bool enemy = false;

        int c = _cellOf[i], cx = c % GridN, cy = c / GridN;
        for (int gy = Math.Max(0, cy - 1); gy <= Math.Min(GridN - 1, cy + 1); gy++)
            for (int gx = Math.Max(0, cx - 1); gx <= Math.Min(GridN - 1, cx + 1); gx++)
            {
                int cell = gy * GridN + gx;
                for (int k = _cellStart[cell]; k < _cellStart[cell + 1]; k++)
                {
                    int j = _sorted[k];
                    if (j == i) continue;
                    float dx = _x[j] - x, dy = _y[j] - y, d2 = dx * dx + dy * dy;
                    if (d2 > Radius * Radius || d2 < 1e-6f) continue;
                    float inv = 1 / MathF.Sqrt(d2);
                    if (d2 < 9) { sepX -= dx * inv; sepY -= dy * inv; }              // colisão: empurra
                    if (_team[j] == team)
                    {
                        aliX += _vx[j]; aliY += _vy[j]; cohX += dx; cohY += dy; allies++;
                    }
                    else
                    {
                        if (d2 < 16) dmg += 4 * Dt * (1 - MathF.Sqrt(d2) / 4);      // corpo a corpo
                        if (d2 < bestD2) { bestD2 = d2; enemyX = dx; enemyY = dy; enemy = true; }
                    }
                }
            }

        // Decisão de IA: vida baixa → foge do inimigo mais próximo; senão persegue.
        float ax = sepX * 60, ay = sepY * 60;
        if (allies > 0)
        {
            ax += (aliX / allies - vx) * 0.5f + cohX / allies * 0.3f;
            ay += (aliY / allies - vy) * 0.5f + cohY / allies * 0.3f;
        }
        if (enemy)
        {
            float inv = 1 / MathF.Sqrt(bestD2), dir = _hp[i] < 30 ? -1 : 1;
            ax += dir * enemyX * inv * 80; ay += dir * enemyY * inv * 80;
        }
        else ax += (team == 0 ? 1 : -1) * 25; // sem inimigo por perto: marcha para a base inimiga

        vx += ax * Dt; vy += ay * Dt;
        float sp = MathF.Sqrt(vx * vx + vy * vy);
        if (sp > 60) { vx *= 60 / sp; vy *= 60 / sp; }
        x += vx * Dt; y += vy * Dt;
        if (x < 0 || x > World) { vx = -vx; x = Math.Clamp(x, 0, World); }
        if (y < 0 || y > World) { vy = -vy; y = Math.Clamp(y, 0, World); }

        _nx[i] = x; _ny[i] = y; _nvx[i] = vx; _nvy[i] = vy;
        _dmg[i] = dmg;
    }

    /// <summary>
    /// Lógica de jogo em série: aplica dano; quem morre ou chega à base inimiga ("pontua") renasce na própria base.
    /// O fluxo contínuo nos dois sentidos mantém uma frente de batalha no meio e a carga estável durante o teste.
    /// </summary>
    private void GameLogic()
    {
        for (int i = 0; i < Entities; i++)
        {
            _hp[i] = MathF.Min(100, _hp[i] - _dmg[i] + 0.5f * Dt);
            bool scored = _team[i] == 0 ? _x[i] > World * 0.97f : _x[i] < World * 0.03f;
            if (_hp[i] > 0 && !scored) continue;
            _hp[i] = 100;
            _x[i] = (_team[i] == 0 ? 0.02f : 0.98f) * World;
            _y[i] = Rnd() * World;
            _vx[i] = (_team[i] == 0 ? 1 : -1) * 25; _vy[i] = 0;
        }
    }

    /// <summary>Preparação do desenho em série: câmera em perspectiva, culling e ordenação de trás para frente.</summary>
    private void PrepareDraw()
    {
        float camX = World / 2 + MathF.Sin(_frames * 0.01f) * 200, camY = -200;
        int visible = 0;
        for (int i = 0; i < Entities; i++)
        {
            float dx = _x[i] - camX, dy = _y[i] - camY;
            if (dy <= 0 || MathF.Abs(dx) > dy * 1.2f) continue; // fora do campo de visão
            _drawKey[visible] = -dy;                            // mais longe primeiro
            _drawList[visible++] = i;
        }
        Array.Sort(_drawKey, _drawList, 0, visible);
    }

    private float Rnd()
    {
        _rng ^= _rng << 13; _rng ^= _rng >> 17; _rng ^= _rng << 5;
        return (_rng >> 8) * (1f / 16777216f);
    }
}
