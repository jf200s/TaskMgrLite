using System.ComponentModel;
using System.Diagnostics;
using System.Text;
using System.Text.Json;

namespace ShellMcp;

/// <summary>
/// 最小 stdio MCP server：只提供一个 run 工具。
///
/// 安全边界：
///   - 工作目录锁在启动参数给出的根目录内，越界直接拒绝；
///   - 每条命令写到 stderr 留痕；
///   - 不做命令白名单（会毁掉可用性），但你可以随时从 MCP 配置里移除本 server。
/// </summary>
internal static class Program
{
    private static string _root = string.Empty;
    private static StreamWriter _out = null!;

    private static int Main(string[] args)
    {
        _root = Path.GetFullPath(args.Length > 0 ? args[0] : Directory.GetCurrentDirectory());

        using var stdin = new StreamReader(Console.OpenStandardInput(), new UTF8Encoding(false));
        using var stdout = new StreamWriter(Console.OpenStandardOutput(), new UTF8Encoding(false))
        {
            AutoFlush = true
        };

        _out = stdout;
        Console.Error.WriteLine($"[shell-mcp] root={_root}");

        string? line;
        while ((line = stdin.ReadLine()) is not null)
        {
            if (string.IsNullOrWhiteSpace(line)) continue;

            try
            {
                Handle(line);
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine("[shell-mcp] " + ex);
            }
        }

        return 0;
    }

    private static void Handle(string line)
    {
        using JsonDocument doc = JsonDocument.Parse(line);
        JsonElement root = doc.RootElement;

        if (!root.TryGetProperty("method", out JsonElement methodElement)) return;

        string? method = methodElement.GetString();
        if (method is null) return;

        // 通知没有 id，不回包
        if (!root.TryGetProperty("id", out JsonElement idElement)) return;

        JsonElement id = idElement.Clone();

        switch (method)
        {
            case "initialize":
                Respond(id, BuildInitialize(root));
                break;

            case "tools/list":
                Respond(id, BuildTools());
                break;

            case "tools/call":
                Respond(id, CallTool(root));
                break;

            case "ping":
                Respond(id, new Dictionary<string, object?>());
                break;

            default:
                RespondError(id, -32601, $"method not found: {method}");
                break;
        }
    }

    private static object BuildInitialize(JsonElement root)
    {
        string version = "2024-11-05";

        if (root.TryGetProperty("params", out JsonElement parameters)
            && parameters.TryGetProperty("protocolVersion", out JsonElement requested)
            && requested.GetString() is { Length: > 0 } text)
        {
            version = text;   // 回显客户端版本，最省事的兼容方式
        }

        return new Dictionary<string, object?>
        {
            ["protocolVersion"] = version,
            ["capabilities"] = new Dictionary<string, object?>
            {
                ["tools"] = new Dictionary<string, object?>()
            },
            ["serverInfo"] = new Dictionary<string, object?>
            {
                ["name"] = "local-shell",
                ["version"] = "1.0.0"
            }
        };
    }

    private static object BuildTools() => new Dictionary<string, object?>
    {
        ["tools"] = new object[]
        {
            new Dictionary<string, object?>
            {
                ["name"] = "run",
                ["description"] =
                    "Run a PowerShell command on the local Windows machine. " +
                    "Working directory must stay inside the configured root.",
                ["inputSchema"] = new Dictionary<string, object?>
                {
                    ["type"] = "object",
                    ["properties"] = new Dictionary<string, object?>
                    {
                        ["command"] = new Dictionary<string, object?>
                        {
                            ["type"] = "string",
                            ["description"] = "PowerShell command line, e.g. \"dotnet --version\""
                        },
                        ["cwd"] = new Dictionary<string, object?>
                        {
                            ["type"] = "string",
                            ["description"] = "Relative subdirectory under the root. Defaults to the root."
                        },
                        ["timeoutSeconds"] = new Dictionary<string, object?>
                        {
                            ["type"] = "integer",
                            ["description"] = "Kill the process after this many seconds. Default 120, max 3600."
                        }
                    },
                    ["required"] = new[] { "command" }
                }
            }
        }
    };

    private static object CallTool(JsonElement root)
    {
        if (!root.TryGetProperty("params", out JsonElement parameters))
            return ErrorContent("missing params");

        string? name = parameters.TryGetProperty("name", out JsonElement nameElement)
            ? nameElement.GetString()
            : null;

        if (name != "run")
            return ErrorContent($"unknown tool: {name}");

        if (!parameters.TryGetProperty("arguments", out JsonElement arguments))
            return ErrorContent("missing arguments");

        string? command = arguments.TryGetProperty("command", out JsonElement commandElement)
            ? commandElement.GetString()
            : null;

        if (string.IsNullOrWhiteSpace(command))
            return ErrorContent("command is required");

        string cwd = _root;

        if (arguments.TryGetProperty("cwd", out JsonElement cwdElement)
            && cwdElement.GetString() is { Length: > 0 } relative)
        {
            cwd = Path.GetFullPath(Path.Combine(_root, relative));

            if (!cwd.StartsWith(_root, StringComparison.OrdinalIgnoreCase))
                return ErrorContent("cwd is outside the allowed root");
        }

        int timeoutSeconds = 120;

        if (arguments.TryGetProperty("timeoutSeconds", out JsonElement timeoutElement)
            && timeoutElement.TryGetInt32(out int requested)
            && requested > 0)
        {
            timeoutSeconds = Math.Min(requested, 3600);
        }

        return RunCommand(command, cwd, timeoutSeconds);
    }

    private static object RunCommand(string command, string cwd, int timeoutSeconds)
    {
        // 用 -EncodedCommand 彻底绕开引号转义问题
        string encoded = Convert.ToBase64String(Encoding.Unicode.GetBytes(command));

        Console.Error.WriteLine($"[shell-mcp] ({cwd}) {command}");

        using Process? process = StartShell(encoded, cwd, out string? startError);

        if (process is null)
            return ErrorContent(startError ?? "failed to start shell");

        Task<string> stdoutTask = process.StandardOutput.ReadToEndAsync();
        Task<string> stderrTask = process.StandardError.ReadToEndAsync();

        bool exited = process.WaitForExit(timeoutSeconds * 1000);

        if (!exited)
        {
            try { process.Kill(entireProcessTree: true); } catch { /* 已退出 */ }
        }

        string stdout = Truncate(stdoutTask.GetAwaiter().GetResult());
        string stderr = Truncate(stderrTask.GetAwaiter().GetResult());
        int exitCode = exited ? process.ExitCode : -1;

        string text =
            $"exit={exitCode}{(exited ? "" : " (timeout, killed)")}\n" +
            "--- stdout ---\n" + stdout + "\n" +
            "--- stderr ---\n" + stderr;

        return new Dictionary<string, object?>
        {
            ["content"] = new object[]
            {
                new Dictionary<string, object?>
                {
                    ["type"] = "text",
                    ["text"] = text
                }
            },
            ["isError"] = exitCode != 0
        };
    }

    private static Process? StartShell(string encodedCommand, string cwd, out string? error)
    {
        error = null;

        foreach (string shell in new[] { "pwsh", "powershell" })
        {
            try
            {
                var info = new ProcessStartInfo
                {
                    FileName = shell,
                    Arguments = "-NoProfile -NonInteractive -EncodedCommand " + encodedCommand,
                    WorkingDirectory = cwd,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    StandardOutputEncoding = Encoding.UTF8,
                    StandardErrorEncoding = Encoding.UTF8
                };

                var process = new Process { StartInfo = info };
                process.Start();
                return process;
            }
            catch (Win32Exception ex)
            {
                error = $"{shell} not available: {ex.Message}";
            }
        }

        return null;
    }

    private static string Truncate(string text) =>
        text.Length <= 100_000 ? text : text[..100_000] + "\n...[truncated]";

    private static object ErrorContent(string message) => new Dictionary<string, object?>
    {
        ["content"] = new object[]
        {
            new Dictionary<string, object?>
            {
                ["type"] = "text",
                ["text"] = message
            }
        },
        ["isError"] = true
    };

    private static void Respond(JsonElement id, object result)
    {
        var message = new Dictionary<string, object?>
        {
            ["jsonrpc"] = "2.0",
            ["id"] = id,
            ["result"] = result
        };

        _out.WriteLine(JsonSerializer.Serialize(message));
    }

    private static void RespondError(JsonElement id, int code, string message)
    {
        var payload = new Dictionary<string, object?>
        {
            ["jsonrpc"] = "2.0",
            ["id"] = id,
            ["error"] = new Dictionary<string, object?>
            {
                ["code"] = code,
                ["message"] = message
            }
        };

        _out.WriteLine(JsonSerializer.Serialize(payload));
    }
}
