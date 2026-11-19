using Byterake.Core;

namespace Byterake.Tests;

public class UninstallTests
{
    [Theory]
    [InlineData("\"C:\\Program Files\\App\\unins000.exe\"", "C:\\Program Files\\App\\unins000.exe", "")]
    [InlineData("\"C:\\Program Files\\App\\uninstall.exe\" /S --x", "C:\\Program Files\\App\\uninstall.exe", "/S --x")]
    [InlineData("MsiExec.exe /X{1234-ABCD}", "MsiExec.exe", "/X{1234-ABCD}")]
    [InlineData("C:\\Tools\\App\\remove.exe /uninstall", "C:\\Tools\\App\\remove.exe", "/uninstall")]
    public void Uninstall_commands_are_split_correctly(string cmd, string exe, string args)
    {
        var p = Uninstall.ParseCommand(cmd);
        Assert.NotNull(p);
        Assert.Equal(exe, p!.Value.Exe);
        Assert.Equal(args, p.Value.Args);
    }

    [Theory]
    [InlineData("")]
    [InlineData("\"C:\\broken path")]
    [InlineData("rundll32 something")]
    public void Unclear_commands_are_rejected(string cmd) => Assert.Null(Uninstall.ParseCommand(cmd));

    [Fact]
    public void Installed_programs_can_be_listed()
    {
        var list = Uninstall.ListPrograms();
        Assert.NotEmpty(list);
        Assert.All(list, p => Assert.False(string.IsNullOrWhiteSpace(p.Name)));
    }
}
