using ApprovalPO.Services.Auth;

var fails = 0;

void Assert(bool ok, string label)
{
    if (ok)
    {
        Console.WriteLine("OK  " + label);
        return;
    }

    fails++;
    Console.WriteLine("FAIL " + label);
}

void Section(string title) => Console.WriteLine("\n== " + title + " ==");

var today = new DateOnly(2026, 8, 14);

Section("1. Days remaining");
Assert(SubscriptionBanner.DaysRemaining("2026-08-24", today) == 10, "10 days left");
Assert(SubscriptionBanner.DaysRemaining("2026-08-15", today) == 1, "1 day left");
Assert(SubscriptionBanner.DaysRemaining("2026-08-14", today) == 0, "expires today");
Assert(SubscriptionBanner.DaysRemaining("2026-08-04", today) == -10, "already expired");
Assert(SubscriptionBanner.DaysRemaining("", today) is null, "empty date -> null");
Assert(SubscriptionBanner.DaysRemaining(null, today) is null, "null date -> null");

Section("2. Countdown copy");
{
    var ctx = SubscriptionBanner.Build("2026-08-24", "ACTIVE", "60183977796", today);
    Assert(ctx.Show && !ctx.Expired, "10-day banner shown");
    Assert(ctx.Title == "Subscription expires in 10 days", "10-day title");
    Assert(ctx.Body.Contains("renew", StringComparison.OrdinalIgnoreCase), "10-day body");
}

{
    var ctx = SubscriptionBanner.Build("2026-08-21", null, "60183977796", today);
    Assert(ctx.Show && !ctx.Expired, "7-day banner shown");
    Assert(ctx.Title == "Your subscription expires in 7 days.", "7-day title");
    Assert(ctx.Body == "", "7-day body empty");
}

{
    var ctx = SubscriptionBanner.Build("2026-08-17", null, "60183977796", today);
    Assert(ctx.Show && !ctx.Expired, "3-day banner shown");
    Assert(ctx.Title == "Your subscription expires in 3 days.", "3-day title");
}

{
    var ctx = SubscriptionBanner.Build("2026-08-15", null, "60183977796", today);
    Assert(ctx.Show && !ctx.Expired, "1-day banner shown");
    Assert(ctx.Title == "Your subscription expires tomorrow.", "1-day title");
}

{
    var ctx = SubscriptionBanner.Build("2027-04-30", null, "60183977796", today);
    Assert(!ctx.Show && !ctx.Expired, "far future: no banner");
}

Section("3. Expired lockout");
{
    var ctx = SubscriptionBanner.Build("2026-08-04", "ACTIVE", "60183977796", today);
    Assert(ctx.Show && ctx.Expired, "expired lockout");
    Assert(ctx.Title == "Subscription Expired", "expired title");
    Assert(ctx.Body == "Your subscription has expired.", "expired body");
    Assert(ctx.Detail.Contains("ProAcc System", StringComparison.Ordinal), "expired detail");
    Assert(ctx.WhatsAppUrl.StartsWith("https://wa.me/60183977796?", StringComparison.Ordinal), "wa.me url");
    Assert(ctx.WhatsAppUrl.Contains("e-Approval", StringComparison.Ordinal), "prefill mentions e-Approval");
}

Section("4. WhatsApp digits");
Assert(SubscriptionBanner.WhatsAppDigits("018-397 7796") == "60183977796", "01x -> 60x");
Assert(SubscriptionBanner.WhatsAppDigits("+60 18-3977796") == "60183977796", "+60 stripped");
Assert(SubscriptionBanner.WhatsAppDigits("") == "", "empty number");
Assert(SubscriptionBanner.BuildWhatsAppUrl("") == "", "empty url");

Console.WriteLine(fails == 0 ? "\nAll subscription banner smoke tests passed." : $"\n{fails} failure(s).");
Environment.Exit(fails == 0 ? 0 : 1);
