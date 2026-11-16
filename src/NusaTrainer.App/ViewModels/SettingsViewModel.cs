using NusaTrainer.App.Services;

namespace NusaTrainer.App.ViewModels;

public sealed class SettingsViewModel : ViewModelBase
{
    private readonly AppServices _services;
    private readonly Action _onSaved;
    private string _databaseUrl = "";
    private string _publicKeyPem = "";
    private bool _requireSignature;
    private bool _autoUpdate;
    private bool _checkAppUpdates;
    private string? _error;

    public event Action? Closed;

    public RelayCommand SaveCommand { get; }
    public RelayCommand CancelCommand { get; }

    public SettingsViewModel(AppServices services, Action onSaved)
    {
        _services = services;
        _onSaved = onSaved;
        SaveCommand = new RelayCommand(Save);
        CancelCommand = new RelayCommand(() => Closed?.Invoke());
    }

    public string DatabaseUrl { get => _databaseUrl; set => Set(ref _databaseUrl, value); }
    public string PublicKeyPem { get => _publicKeyPem; set => Set(ref _publicKeyPem, value); }
    public bool RequireSignature { get => _requireSignature; set => Set(ref _requireSignature, value); }
    public bool AutoUpdateOnStart { get => _autoUpdate; set => Set(ref _autoUpdate, value); }
    public bool CheckAppUpdates { get => _checkAppUpdates; set => Set(ref _checkAppUpdates, value); }
    public string? Error
    {
        get => _error;
        set { if (Set(ref _error, value)) OnPropertyChanged(nameof(HasError)); }
    }
    public bool HasError => !string.IsNullOrEmpty(_error);
    public string DataFolder => Core.Util.AppPaths.DataRoot;

    public void Load()
    {
        var s = _services.Settings;
        DatabaseUrl = s.DatabaseUrl;
        PublicKeyPem = s.PublicKeyPem;
        RequireSignature = s.RequireSignature;
        AutoUpdateOnStart = s.AutoUpdateOnStart;
        CheckAppUpdates = s.CheckAppUpdates;
        Error = null;
    }

    private void Save()
    {
        var url = DatabaseUrl.Trim();
        if (url.Length > 0 && (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || uri.Scheme != Uri.UriSchemeHttps))
        {
            Error = "URL database harus diawali https://";
            return;
        }
        var pem = PublicKeyPem.Trim();
        if (pem.Length > 0)
        {
            try
            {
                using var ecdsa = System.Security.Cryptography.ECDsa.Create();
                ecdsa.ImportFromPem(pem);
            }
            catch
            {
                Error = "Public key tidak valid (harus PEM: -----BEGIN PUBLIC KEY-----).";
                return;
            }
        }
        if (RequireSignature && pem.Length == 0 && url.Length > 0)
        {
            Error = "Wajib tanda tangan aktif tetapi public key kosong.";
            return;
        }

        var s = _services.Settings;
        s.DatabaseUrl = url;
        s.PublicKeyPem = pem;
        s.RequireSignature = RequireSignature;
        s.AutoUpdateOnStart = AutoUpdateOnStart;
        s.CheckAppUpdates = CheckAppUpdates;
        _services.SaveSettings();
        Closed?.Invoke();
        _onSaved();
    }
}
