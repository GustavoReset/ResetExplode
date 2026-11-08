using System.IO;
using System.Windows;
using System.Windows.Controls;
using Microsoft.Win32;

namespace FortKnox;

/// <summary>
/// Página "Vault de senhas". Só cuida de tela: lê e grava pelo PasswordVault (async) e copia pelo SecureClipboard.
/// Nenhum método aqui toca o disco ou faz criptografia na thread da interface.
/// </summary>
public partial class VaultView : UserControl
{
    private PasswordVault? _vault;
    private Guid? _editingId;      // null = registro novo
    private string _backupMode = ""; // "export" | "import"
    private string _importFile = "";
    private bool _busy;
    private bool _locked;

    public VaultView() => InitializeComponent();

    /// <summary>
    /// Exigência de autenticação antes de abrir (Windows Hello). Definida pela janela. Null = abre direto (testes).
    /// O arquivo do cofre só é lido DEPOIS que isto devolver "verificado".
    /// </summary>
    public Func<Task<HelloResult>>? Gate { get; set; }

    /// <summary>Disparado quando o usuário clica em BLOQUEAR: a janela deve esquecer a verificação do Hello.</summary>
    public event Action? ManualLock;

    private bool _authenticating;
    private int _epoch; // sobe a cada Lock(): uma abertura que terminou depois de bloquear é descartada

    // ------------------------------------------------------------------ Ciclo de vida (chamado pela MainWindow.Vault.cs)

    /// <summary>
    /// Verifica o Windows Hello (se houver Gate), carrega o cofre em segundo plano e mostra a lista.
    /// Seguro chamar de novo a cada vez que a página abre. <paramref name="skipGate"/> = "abrir sem Hello só desta vez".
    /// </summary>
    public async Task OpenAsync(PasswordVault vault, bool skipGate = false)
    {
        _vault = vault;
        if (_busy || _authenticating) return;
        int epoch = _epoch;

        if (!skipGate && Gate != null)
        {
            _locked = true;
            VBtnLock.Content = "DESBLOQUEAR";
            VStatus.Text = "Confirme no Windows Hello para abrir o Vault…";
            UpdateButtons();
            _authenticating = true;
            HelloResult result;
            try { result = await Gate(); }
            catch (Exception ex) { result = new HelloResult(HelloOutcome.Failed, "Erro ao verificar o Windows Hello: " + ex.Message); }
            finally { _authenticating = false; }

            if (epoch != _epoch) return; // o usuário saiu da página enquanto o diálogo estava aberto
            if (!result.IsVerified) { ShowAuthRequired(result); return; }
        }

        VAuthBanner.Visibility = Visibility.Collapsed;
        _locked = false;
        VBtnLock.Content = "BLOQUEAR";

        SetBusy(true);
        VStatus.Text = "Abrindo o cofre…";
        try { await vault.LoadAsync(); }
        catch (Exception ex) { VStatus.Text = "Não foi possível abrir o cofre: " + ex.Message; }
        finally { SetBusy(false); }
        if (epoch != _epoch) { vault.Lock(); return; }
        Refresh();
    }

    private void ShowAuthRequired(HelloResult r)
    {
        VGrid.ItemsSource = null;
        VEmpty.Visibility = Visibility.Collapsed;
        VAuthText.Text = r.Message;
        VBtnOpenAnyway.Visibility = r.Outcome == HelloOutcome.Unavailable ? Visibility.Visible : Visibility.Collapsed; // só quando o PC não tem Hello
        VAuthBanner.Visibility = Visibility.Visible;
        VStatus.Text = r.Outcome == HelloOutcome.Unavailable ? "Windows Hello indisponível." : "Vault bloqueado.";
        UpdateButtons();
    }

    private async void VUnlock_Click(object sender, RoutedEventArgs e)
    {
        if (_vault != null) await OpenAsync(_vault);
    }

    private async void VOpenAnyway_Click(object sender, RoutedEventArgs e)
    {
        if (_vault != null) await OpenAsync(_vault, skipGate: true);
    }

    /// <summary>Esvazia a tela e a memória: lista, formulário, senhas digitadas e o próprio cofre.</summary>
    public void Lock()
    {
        _epoch++;
        _vault?.Lock();
        _locked = true;
        VAuthBanner.Visibility = Visibility.Collapsed;
        VGrid.ItemsSource = null;
        HideEditor();
        HideBackupPanel();
        VSearch.Clear();
        VBanner.Visibility = Visibility.Collapsed;
        VEmpty.Visibility = Visibility.Collapsed;
        VBtnLock.Content = "DESBLOQUEAR";
        VStatus.Text = "Cofre bloqueado. Clique em DESBLOQUEAR para abrir de novo.";
        UpdateButtons();
    }

    // ------------------------------------------------------------------ Lista

    /// <summary>Redesenha a lista. <paramref name="note"/> (resultado da última ação) vai no fim da linha de status.</summary>
    private void Refresh(string? note = null)
    {
        if (_vault == null || _locked) return;
        var state = _vault.State;
        bool ready = state == VaultState.Ready;

        VBanner.Visibility = state == VaultState.Unreadable ? Visibility.Visible : Visibility.Collapsed;
        VBannerText.Text = _vault.Problem ?? "";

        var items = ready ? _vault.Search(VSearch.Text) : new List<VaultItem>();
        VGrid.ItemsSource = items;
        VEmpty.Visibility = ready && items.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        VEmpty.Text = _vault.Count == 0 ? "Nenhum registro. Clique em NOVA SENHA para começar." : "Nada encontrado para essa busca.";

        VStatus.Text = state switch
        {
            VaultState.Ready => (items.Count == _vault.Count ? $"{_vault.Count} registro(s)" : $"{items.Count} de {_vault.Count} registro(s)")
                                + " · cifrado com o DPAPI da sua conta do Windows",
            VaultState.Unreadable => "O cofre não pôde ser aberto — veja o aviso abaixo.",
            _ => "",
        };
        if (ready && _vault.Problem != null) VStatus.Text += " · ATENÇÃO: " + _vault.Problem; // aberto a partir do .bak
        if (note != null) VStatus.Text += " · " + note;
        UpdateButtons();
    }

    private void UpdateButtons()
    {
        bool ready = _vault is { State: VaultState.Ready } && !_locked;
        VBtnNew.IsEnabled = ready;
        VBtnExport.IsEnabled = ready && _vault!.Count > 0;
        VBtnImport.IsEnabled = _vault != null && !_locked && _vault.State != VaultState.NotLoaded;
        VSearch.IsEnabled = ready;
        VBtnLock.IsEnabled = _vault != null;
    }

    private void SetBusy(bool busy)
    {
        _busy = busy;
        VRoot.IsEnabled = !busy;
        VRoot.Cursor = busy ? System.Windows.Input.Cursors.Wait : null;
    }

    private void VSearch_Changed(object sender, TextChangedEventArgs e) => Refresh(); // filtro em memória: instantâneo

    private async void VLock_Click(object sender, RoutedEventArgs e)
    {
        if (_vault == null) return;
        if (_locked) { await OpenAsync(_vault); return; }
        SecureClipboard.ClearNow(); // bloquear também tira a senha copiada da área de transferência
        Lock();
        ManualLock?.Invoke(); // bloqueio manual = a próxima abertura pede o Hello de novo, sem tolerância
    }

    private static Guid IdOf(object sender) => (Guid)((FrameworkElement)sender).Tag;

    // ------------------------------------------------------------------ Copiar (limpa em 30 s)

    private async void VCopyPass_Click(object sender, RoutedEventArgs e)
        => await CopyAsync(_vault?.GetPassword(IdOf(sender)), "Senha");

    private async void VCopyUser_Click(object sender, RoutedEventArgs e)
        => await CopyAsync(_vault?.GetUsername(IdOf(sender)), "Usuário");

    private async Task CopyAsync(string? value, string what)
    {
        if (string.IsNullOrEmpty(value)) { VStatus.Text = $"{what} vazio neste registro."; return; }
        bool ok = await SecureClipboard.CopyAsync(value,
            onCleared: () => { if (!_locked) VStatus.Text = "Área de transferência limpa."; });
        VStatus.Text = ok
            ? $"{what} copiado. A área de transferência será limpa em {SecureClipboard.Lifetime.TotalSeconds:0} s."
            : "Outro programa está usando a área de transferência. Tente de novo.";
    }

    // ------------------------------------------------------------------ Criar / editar / excluir

    private void VNew_Click(object sender, RoutedEventArgs e) => ShowEditor(null);

    private void VEdit_Click(object sender, RoutedEventArgs e)
    {
        var entry = _vault?.Get(IdOf(sender));
        if (entry != null) ShowEditor(entry);
    }

    private void ShowEditor(VaultEntry? entry)
    {
        HideBackupPanel();
        _editingId = entry?.Id;
        VShow.IsChecked = false; // primeiro: o handler copia o conteúdo antigo entre os campos de senha; depois preenchemos
        VEditorTitle.Text = entry == null ? "NOVO REGISTRO" : "EDITAR REGISTRO";
        VTitle.Text = entry?.Title ?? "";
        VUser.Text = entry?.Username ?? "";
        VPass.Password = entry?.Password ?? "";
        VPassText.Text = "";
        VUrl.Text = entry?.Url ?? "";
        VCategory.Text = entry?.Category ?? "";
        VNotes.Text = entry?.Notes ?? "";
        VEditor.Visibility = Visibility.Visible;
        VTitle.Focus();
    }

    /// <summary>Fecha o formulário e apaga o que foi digitado (a senha não fica nos controles).</summary>
    private void HideEditor()
    {
        VEditor.Visibility = Visibility.Collapsed;
        _editingId = null;
        VTitle.Clear(); VUser.Clear(); VUrl.Clear(); VCategory.Clear(); VNotes.Clear();
        VPass.Clear(); VPassText.Clear();
        VShow.IsChecked = false;
    }

    private void VCancel_Click(object sender, RoutedEventArgs e) => HideEditor();

    private void VPass_Changed(object sender, RoutedEventArgs e)
    {
        if (VPassHint != null) VPassHint.Visibility = VPass.Password.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    /// <summary>Alterna entre PasswordBox e TextBox mantendo o mesmo conteúdo.</summary>
    private void VShow_Changed(object sender, RoutedEventArgs e)
    {
        if (VPassText == null) return;
        bool show = VShow.IsChecked == true;
        if (show) VPassText.Text = VPass.Password; else VPass.Password = VPassText.Text;
        VPass.Visibility = VPassHint.Visibility = show ? Visibility.Collapsed : Visibility.Visible;
        VPassText.Visibility = show ? Visibility.Visible : Visibility.Collapsed;
        if (!show) { VPassText.Clear(); VPass_Changed(this, e); }
    }

    private void VGenerate_Click(object sender, RoutedEventArgs e)
    {
        string pwd = PasswordGenerator.Generate(20);
        VPass.Password = pwd;
        VShow.IsChecked = true; // mostra para o usuário ver o que foi gerado
        VPassText.Text = pwd;
    }

    private async void VSave_Click(object sender, RoutedEventArgs e)
    {
        if (_vault == null || _busy) return;
        var entry = new VaultEntry
        {
            Id = _editingId ?? Guid.Empty,
            Title = VTitle.Text,
            Username = VUser.Text,
            Password = VShow.IsChecked == true ? VPassText.Text : VPass.Password,
            Url = VUrl.Text,
            Category = VCategory.Text,
            Notes = VNotes.Text,
        };

        SetBusy(true);
        try
        {
            if (_editingId != null) await _vault.UpdateAsync(entry);
            else await _vault.AddAsync(entry);
            HideEditor();
        }
        catch (ArgumentException ex) { VStatus.Text = ex.Message; return; } // ex.: nome vazio — o formulário continua aberto
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException or System.Security.Cryptography.CryptographicException)
        { VStatus.Text = "Não foi possível salvar: " + ex.Message; return; }
        finally { SetBusy(false); }
        Refresh("salvo");
    }

    private async void VDelete_Click(object sender, RoutedEventArgs e)
    {
        if (_vault == null || _busy) return;
        var id = IdOf(sender);
        var entry = _vault.Get(id);
        if (entry == null) return;
        var ask = MessageBox.Show($"Excluir \"{entry.Title}\" do cofre? Isso não pode ser desfeito.", "Vault de senhas",
                                  MessageBoxButton.YesNo, MessageBoxImage.Warning, MessageBoxResult.No);
        if (ask != MessageBoxResult.Yes) return;

        SetBusy(true);
        string note = "excluído";
        try { await _vault.DeleteAsync(id); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException)
        { note = "não foi possível excluir: " + ex.Message; }
        finally { SetBusy(false); }
        Refresh(note);
    }

    // ------------------------------------------------------------------ Backup (plano B se o DPAPI falhar)

    private void VExport_Click(object sender, RoutedEventArgs e)
    {
        HideEditor();
        _backupMode = "export";
        VBackupTitle.Text = "EXPORTAR BACKUP";
        VBackupHint.Text = "O backup é cifrado com uma senha que SÓ você sabe (AES-256). Guarde-a em outro lugar: sem ela o backup não abre. " +
                           "Use um backup quando for redefinir a senha do Windows por outro administrador, recriar o perfil ou trocar de PC.";
        ShowBackupPanel(repeat: true);
    }

    private void VImport_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new OpenFileDialog { Filter = "Backup do Vault (*.fkvault)|*.fkvault|Todos os arquivos|*.*", Title = "Escolher backup do Vault" };
        if (dlg.ShowDialog(Window.GetWindow(this)) != true) return;
        HideEditor();
        _backupMode = "import";
        _importFile = dlg.FileName;
        VBackupTitle.Text = "IMPORTAR BACKUP";
        VBackupHint.Text = "Digite a senha escolhida ao exportar este backup. Registros com o mesmo ID são mesclados (vale o mais recente).";
        ShowBackupPanel(repeat: false);
    }

    private void ShowBackupPanel(bool repeat)
    {
        VBackupPass.Clear(); VBackupPass2.Clear();
        VBackupRepeat.Visibility = repeat ? Visibility.Visible : Visibility.Collapsed;
        VBackupPanel.Visibility = Visibility.Visible;
        VBackupPass.Focus();
    }

    private void HideBackupPanel()
    {
        VBackupPanel.Visibility = Visibility.Collapsed;
        VBackupPass.Clear(); VBackupPass2.Clear();
        _backupMode = ""; _importFile = "";
    }

    private void VBackupCancel_Click(object sender, RoutedEventArgs e) => HideBackupPanel();

    private async void VBackupOk_Click(object sender, RoutedEventArgs e)
    {
        if (_vault == null || _busy) return;
        string pass = VBackupPass.Password;
        string? savePath = null;

        if (_backupMode == "export")
        {
            if (pass != VBackupPass2.Password) { VStatus.Text = "As duas senhas do backup não são iguais."; return; }
            var dlg = new SaveFileDialog
            {
                Filter = "Backup do Vault (*.fkvault)|*.fkvault", Title = "Salvar backup do Vault",
                FileName = $"vault-{DateTime.Now:yyyyMMdd}.fkvault",
            };
            if (dlg.ShowDialog(Window.GetWindow(this)) != true) return;
            savePath = dlg.FileName;
        }

        SetBusy(true);
        string? note;
        bool ok = false;
        try
        {
            if (_backupMode == "export")
            {
                VStatus.Text = "Cifrando o backup…";
                await _vault.ExportBackupAsync(savePath!, pass);
                note = $"backup salvo em {savePath} (guarde a senha dele em outro lugar)";
            }
            else
            {
                VStatus.Text = "Abrindo o backup…";
                int n = await _vault.ImportBackupAsync(_importFile, pass);
                note = $"{n} registro(s) importado(s)";
            }
            ok = true;
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidDataException or InvalidOperationException
                                       or IOException or UnauthorizedAccessException)
        { note = ex.Message; } // senha errada, arquivo inválido, disco cheio…; o painel continua aberto para tentar de novo
        finally { SetBusy(false); }

        if (ok) HideBackupPanel();
        Refresh(note);
    }
}
