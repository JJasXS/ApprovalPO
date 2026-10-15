namespace ApprovalPO.Tests.ApprovalDbSmoke;

internal sealed class SmokeReport
{
    public int Passed { get; private set; }
    public int Failed { get; private set; }

    public void Section(string title)
    {
        Console.WriteLine();
        Console.ForegroundColor = ConsoleColor.Cyan;
        Console.WriteLine("=== " + title + " ===");
        Console.ResetColor();
    }

    public void Pass(string label)
    {
        Passed++;
        Console.ForegroundColor = ConsoleColor.Green;
        Console.WriteLine("  PASS  " + label);
        Console.ResetColor();
    }

    public void Fail(string label, string detail)
    {
        Failed++;
        Console.ForegroundColor = ConsoleColor.Red;
        Console.WriteLine("  FAIL  " + label + "  " + detail);
        Console.ResetColor();
    }

    public void Check(bool ok, string label, string detail)
    {
        if (ok) Pass(label);
        else Fail(label, detail);
    }

    public void PrintSummary()
    {
        Console.WriteLine();
        Console.WriteLine($"PASSED {Passed}  FAILED {Failed}");
    }
}
