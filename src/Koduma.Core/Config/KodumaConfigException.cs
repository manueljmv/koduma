namespace Koduma.Core.Config;

public class KodumaConfigException : Exception
{
    public KodumaConfigException(string message, Exception? inner = null) : base(message, inner) { }
}
