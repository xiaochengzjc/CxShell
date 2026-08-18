using CxShell.Models;
using CxShell.Services;

namespace CxShell.ViewModels;

public sealed class SshHostKeyPromptViewModel
{
    private readonly LocalizationService _localization = LocalizationService.Shared;

    public SshHostKeyPromptViewModel(SshHostKeyPromptRequest request)
    {
        Request = request;
    }

    public SshHostKeyPromptRequest Request { get; }
    public bool IsChanged => Request.Verification == SshHostKeyVerification.Changed;
    public string TitleText => _localization.Text("SshHostKey.Title");
    public string WarningText => _localization.Text(IsChanged ? "SshHostKey.ChangedWarning" : "SshHostKey.UnknownWarning");
    public string HostLabelText => _localization.Text("SshHostKey.Host");
    public string KeyTypeLabelText => _localization.Text("SshHostKey.KeyType");
    public string KeyLengthLabelText => _localization.Text("SshHostKey.KeyLength");
    public string FingerprintLabelText => _localization.Text("SshHostKey.Fingerprint");
    public string PreviousFingerprintLabelText => _localization.Text("SshHostKey.PreviousFingerprint");
    public string HostText => $"{Request.Observation.Host}:{Request.Observation.Port}";
    public string KeyTypeText => Request.Observation.KeyType;
    public string KeyLengthText => $"{Request.Observation.KeyLength} bit";
    public string FingerprintText => Request.Observation.Fingerprint;
    public string PreviousFingerprintText => Request.PreviousFingerprint ?? string.Empty;
    public string CancelText => _localization.Text("SshHostKey.Cancel");
    public string TrustOnceText => _localization.Text("SshHostKey.TrustOnce");
    public string TrustPermanentlyText => _localization.Text("SshHostKey.TrustPermanently");
}
