namespace XelsTweaks.Tweaks;

internal interface IRequiresEnableAgreement
{
    bool RequiresEnableAgreement { get; }
    string EnableAgreementTitle { get; }
    string EnableAgreementText { get; }
    string EnableAgreementCheckboxLabel { get; }

    void AcceptEnableAgreement();
}
