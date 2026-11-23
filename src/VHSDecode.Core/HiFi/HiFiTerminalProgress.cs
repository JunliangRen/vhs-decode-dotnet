using System.Globalization;
using System.Runtime.InteropServices;
using System.Text;

namespace VHSDecode.Core.HiFi;

internal sealed partial class HiFiTerminalProgress(
    TextWriter output,
    Func<int> viewportWidth,
    Action? restoreConsoleMode = null) : IDisposable
{
    private const int FrameLines = 7;
    private const int StandardOutputHandle = -11;
    private const uint EnableVirtualTerminalProcessing = 0x0004;
    private const string ClearLine = "\u001b[2K";
    private const string ClearLineEnd = "\u001b[K";
    private static readonly string MoveToFrameStart =
        "\u001b[" + FrameLines.ToString(CultureInfo.InvariantCulture) + "F";
    private int _renderedWidth;

    internal static HiFiTerminalProgress? TryCreate(TextWriter output)
    {
        if (!ReferenceEquals(output, Console.Out) || Console.IsOutputRedirected
            || Environment.GetEnvironmentVariable("TERM") == "dumb" || GetViewportWidth() == 0)
        {
            return null;
        }

        if (!OperatingSystem.IsWindows())
        {
            return new HiFiTerminalProgress(output, GetViewportWidth);
        }

        nint handle = GetStdHandle(StandardOutputHandle);
        if (!GetConsoleMode(handle, out uint mode))
        {
            return null;
        }

        if ((mode & EnableVirtualTerminalProcessing) != 0)
        {
            return new HiFiTerminalProgress(output, GetViewportWidth);
        }

        return SetConsoleMode(handle, mode | EnableVirtualTerminalProcessing)
            ? new HiFiTerminalProgress(output, GetViewportWidth, () => SetConsoleMode(handle, mode))
            : null;
    }

    internal void Render(string frame)
    {
        int width = viewportWidth();
        var buffer = new StringBuilder();
        bool replace = MoveToPreviousFrame(buffer, width);
        if (width == 0)
        {
            buffer.Append(frame);
        }
        else
        {
            // Avoid terminal auto-wrap, including when the viewport is narrow.
            ReadOnlySpan<char> remaining = frame;
            while (!remaining.IsEmpty)
            {
                int lineEnd = remaining.IndexOf('\n');
                ReadOnlySpan<char> line = remaining[..lineEnd].TrimEnd('\r');
                buffer.Append(line[..Math.Min(line.Length, width - 1)]);
                if (replace)
                {
                    // Erase leftover text after writing the new row to avoid blanking it first.
                    buffer.Append(ClearLineEnd);
                }

                buffer.AppendLine();
                remaining = remaining[(lineEnd + 1)..];
            }

            _renderedWidth = width;
        }

        output.Write(buffer.ToString());
        output.Flush();
    }

    internal void Clear()
    {
        var buffer = new StringBuilder();
        if (!MoveToPreviousFrame(buffer, viewportWidth()))
        {
            return;
        }

        for (int row = 0; row < FrameLines; row++)
        {
            buffer.Append(ClearLine);
            buffer.Append("\u001b[1E");
        }

        buffer.Append(MoveToFrameStart);
        output.Write(buffer.ToString());
    }

    private bool MoveToPreviousFrame(StringBuilder buffer, int width)
    {
        // Resizing can reflow old lines. Leave that snapshot in history and
        // start a fresh frame so cursor movement never targets an unknown row.
        bool replace = width != 0 && width == _renderedWidth;
        if (replace)
        {
            buffer.Append(MoveToFrameStart);
        }

        _renderedWidth = 0;
        return replace;
    }

    public void Dispose() => restoreConsoleMode?.Invoke();

    private static int GetViewportWidth()
    {
        try
        {
            int width = Console.WindowWidth;
            return Console.WindowHeight > FrameLines && width > 1 ? width : 0;
        }
        catch (IOException)
        {
            return 0;
        }
    }

    [LibraryImport("kernel32.dll")]
    private static partial nint GetStdHandle(int standardHandle);

    [LibraryImport("kernel32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool GetConsoleMode(nint handle, out uint mode);

    [LibraryImport("kernel32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool SetConsoleMode(nint handle, uint mode);
}
