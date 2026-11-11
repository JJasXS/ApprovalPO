namespace ApprovalPO.Services.Auth;

/// <summary>Custom cookie claims for SY_USER approval UDFs.</summary>
public static class ApprovalClaimTypes
{
    public const string AppRole = "app_role";
    public const string AppLevel = "app_level";
    public const string AppFrom = "app_from";
    public const string AppTo = "app_to";
    public const string IsDirector = "is_director";
    public const string IsAdmin = "is_admin";
}
