using ApprovalPO.Options;

namespace ApprovalPO.Hosting;

/// <summary>Blocks dangerous Approval flags in Production and warns on guide-capture enablement.</summary>
public static class ProductionSafetyExtensions
{
    public static void EnforceProductionSafety(this WebApplication app)
    {
        var cfg = app.Configuration;
        var logger = app.Logger;
        var isProd = app.Environment.IsProduction();

        var skipSy = cfg.GetValue("Approval:SkipSyUserEmailCheck", false);
        var exposeOtp = cfg.GetValue("Approval:ExposeOtpWhenEmailFails", false);
        var debugOtp = cfg.GetValue("Approval:DebugOtpToConsole", false);
        var guideOn = string.Equals(
            Environment.GetEnvironmentVariable("APPROVAL_GUIDE_CAPTURE"),
            "1",
            StringComparison.Ordinal);
        var guideSecret = (cfg["Approval:GuideCaptureSharedSecret"] ?? "").Trim();
        var allowOtpEnv = (Environment.GetEnvironmentVariable("APPROVALPO_ALLOW_OTP_WITHOUT_EMAIL") ?? "").Trim();
        var otpBypassEnv = allowOtpEnv.Equals("true", StringComparison.OrdinalIgnoreCase)
            || allowOtpEnv == "1"
            || allowOtpEnv.Equals("yes", StringComparison.OrdinalIgnoreCase);

        if (isProd)
        {
            if (skipSy)
                throw new InvalidOperationException(
                    "Approval:SkipSyUserEmailCheck must be false in Production.");
            if (exposeOtp)
                throw new InvalidOperationException(
                    "Approval:ExposeOtpWhenEmailFails must be false in Production.");
            if (debugOtp)
                throw new InvalidOperationException(
                    "Approval:DebugOtpToConsole must be false in Production.");
            if (otpBypassEnv)
                throw new InvalidOperationException(
                    "APPROVALPO_ALLOW_OTP_WITHOUT_EMAIL must not be set in Production.");
            if (guideOn && guideSecret.Length < 16)
                throw new InvalidOperationException(
                    "APPROVAL_GUIDE_CAPTURE=1 requires Approval:GuideCaptureSharedSecret (16+ chars) in Production.");
        }

        if (guideOn)
            logger.LogWarning(
                "APPROVAL_GUIDE_CAPTURE is enabled. Guide login requires X-Guide-Capture-Secret and does not elevate roles.");

        var sendIp = cfg.GetValue("Approval:LoginMaxSendOtpPerIpPerMinute", 0);
        var verifyIp = cfg.GetValue("Approval:LoginMaxVerifyOtpPerIpPerMinute", 0);
        if (isProd && (sendIp <= 0 || verifyIp <= 0))
            logger.LogWarning(
                "Production IP OTP throttle is disabled (LoginMaxSend/VerifyOtpPerIpPerMinute <= 0). Prefer enabling per SECURITY_HARDENING.md.");

        ApplyApprovalBands(cfg, logger);
    }

    private static void ApplyApprovalBands(IConfiguration cfg, ILogger logger)
    {
        var l1 = cfg.GetValue<decimal?>("Approval:ApprovalBandLevel1Max");
        var l2 = cfg.GetValue<decimal?>("Approval:ApprovalBandLevel2Max");
        var l3 = cfg.GetValue<decimal?>("Approval:ApprovalBandLevel3Max");
        if (l1 is null && l2 is null && l3 is null)
            return;

        ApprovalPO.Services.Auth.ApprovalLevelWorkflow.ConfigureBands(
            l1 ?? ApprovalPO.Services.Auth.ApprovalLevelWorkflow.BandLevel1Max,
            l2 ?? ApprovalPO.Services.Auth.ApprovalLevelWorkflow.BandLevel2Max,
            l3 ?? ApprovalPO.Services.Auth.ApprovalLevelWorkflow.BandLevel3Max);
        logger.LogInformation(
            "Approval amount bands: L1<={L1} L2<={L2} L3<={L3}",
            ApprovalPO.Services.Auth.ApprovalLevelWorkflow.BandLevel1Max,
            ApprovalPO.Services.Auth.ApprovalLevelWorkflow.BandLevel2Max,
            ApprovalPO.Services.Auth.ApprovalLevelWorkflow.BandLevel3Max);
    }
}
