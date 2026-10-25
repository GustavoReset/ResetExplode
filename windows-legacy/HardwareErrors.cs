namespace ResetExplode;

/// <summary>Erros de hardware lidos do registro de eventos do Windows (WHEA e reinício do driver de vídeo).</summary>
public sealed record HardwareErrors(int Whea, int GpuResets, string? LastDetail, string? Error)
{
    public int Total => Whea + GpuResets;
}
