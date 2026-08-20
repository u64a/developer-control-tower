namespace ControlTower.Core.Contracts
{
    public interface ISshService
    {
        SshResult TestConnection(string host, int port, string user, string password);

        SshResult CreateDirectory(string host, int port, string user, string password, string remotePath);

        SshResult RunCommand(string host, int port, string user, string password, string command);
    }

    public class SshResult
    {
        public bool Success { get; init; }
        public string Output { get; init; } = string.Empty;
        public string Error { get; init; } = string.Empty;

        /// <summary>
        /// Machine-readable failure code (e.g. <c>ssh/no-host-key-store</c>,
        /// <c>ssh/host-key-mismatch</c>). Empty for success or legacy failures.
        /// </summary>
        public string Code { get; init; } = string.Empty;

        public static SshResult Ok(string output = "") => new() { Success = true, Output = output };
        public static SshResult Fail(string error) => new() { Success = false, Error = error };
        public static SshResult Fail(string code, string error) => new() { Success = false, Code = code ?? string.Empty, Error = error ?? string.Empty };
        public static SshResult Unavailable(string code, string error) => new() { Success = false, Code = code ?? string.Empty, Error = error ?? string.Empty };
    }
}
