namespace SchemaGenerator.Models;

/// <summary>
/// Command-line and runtime execution options for the legacy schema ingestion pipeline.
/// </summary>
public sealed record PipelineRunOptions(
    bool Resume = false,
    bool Fresh = false,
    bool NonInteractive = false,
    bool AutoConfirm = false,
    string TenantId = "ALL")
{
    public static PipelineRunOptions ParseArgs(string[]? args)
    {
        if (args is null || args.Length == 0)
        {
            return new PipelineRunOptions();
        }

        bool resume = false;
        bool fresh = false;
        bool nonInteractive = false;
        bool autoConfirm = false;
        string tenantId = "ALL";

        for (int i = 0; i < args.Length; i++)
        {
            var arg = args[i];
            if (string.IsNullOrWhiteSpace(arg))
            {
                continue;
            }

            if (arg.Equals("--resume", StringComparison.OrdinalIgnoreCase))
            {
                resume = true;
            }
            else if (arg.Equals("--fresh", StringComparison.OrdinalIgnoreCase))
            {
                fresh = true;
            }
            else if (arg.Equals("--non-interactive", StringComparison.OrdinalIgnoreCase))
            {
                nonInteractive = true;
            }
            else if (arg.Equals("--yes", StringComparison.OrdinalIgnoreCase) ||
                     arg.Equals("-y", StringComparison.OrdinalIgnoreCase))
            {
                autoConfirm = true;
            }
            else if (arg.StartsWith("--tenant=", StringComparison.OrdinalIgnoreCase))
            {
                var value = arg["--tenant=".Length..].Trim();
                if (!string.IsNullOrEmpty(value))
                {
                    tenantId = value;
                }
            }
            else if (arg.Equals("--tenant", StringComparison.OrdinalIgnoreCase) && i + 1 < args.Length)
            {
                var value = args[++i]?.Trim();
                if (!string.IsNullOrEmpty(value))
                {
                    tenantId = value;
                }
            }
        }

        return new PipelineRunOptions(resume, fresh, nonInteractive, autoConfirm, tenantId);
    }
}
