namespace ApprovalPO.Hosting;

/// <summary>Kestrel HTTP/HTTPS ports from <c>Approval:PublicHttpPort</c> / <c>Approval:PublicHttpsPort</c>.</summary>
internal static class ApprovalKestrelPorts
{
    public static int Http(IConfiguration configuration) =>
        configuration.GetValue("Approval:PublicHttpPort", 2095);

    public static int Https(IConfiguration configuration) =>
        configuration.GetValue("Approval:PublicHttpsPort", 2096);
}
