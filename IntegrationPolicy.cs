namespace ResetExplode;

/// <summary>O que disparou uma conexão na aba RESET INTEGRAÇÃO.</summary>
public enum IntegrationTrigger { TabOpened, ConnectAllClicked, CorsairActivateClicked, CorsairSdkInstalled }

/// <summary>Quais pontes conectar. OpenRGB e Razer só abrem socket em localhost; só a Corsair carrega código (a DLL do SDK).</summary>
public sealed record ConnectPlan(bool Corsair, bool OpenRgb, bool Razer);

/// <summary>
/// Spec 001, G1: abrir a aba nunca carrega a DLL da Corsair. Ela só entra no processo depois de o usuário clicar em
/// ATIVAR CONTROLE CORSAIR nesta sessão; a ativação não é gravada nas configurações.
/// </summary>
public static class IntegrationPolicy
{
    public static ConnectPlan For(IntegrationTrigger trigger, bool corsairActivated) => trigger switch
    {
        IntegrationTrigger.TabOpened => new ConnectPlan(Corsair: false, OpenRgb: true, Razer: true),
        IntegrationTrigger.ConnectAllClicked => new ConnectPlan(Corsair: corsairActivated, OpenRgb: true, Razer: true),
        IntegrationTrigger.CorsairActivateClicked => new ConnectPlan(Corsair: true, OpenRgb: false, Razer: false),
        IntegrationTrigger.CorsairSdkInstalled => new ConnectPlan(Corsair: corsairActivated, OpenRgb: false, Razer: false),
        _ => new ConnectPlan(false, false, false),
    };
}
