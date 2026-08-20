namespace ControlTower.Core.Contracts
{
    public interface ICredentialStore
    {
        string GetPassword(string target);

        void SetPassword(string target, string password);

        void DeletePassword(string target);
    }
}
