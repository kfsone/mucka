namespace Mucka.Core;

public record PasswordPromptArgs(string ProfileName, string Host, int Port, string AccountId);
public record PasswordResult(string Password, bool Remember);
