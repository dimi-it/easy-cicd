namespace EasyCicd.Deploy;

public record CommandResult(int ExitCode, string StdOut, string StdErr)
{
    public bool IsSuccess => ExitCode == 0;
}
