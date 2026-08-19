namespace QuickPhrase.Platform.Windows;

/// <summary>数据层错误携带稳定错误码，Desktop 可把它转换为中文用户提示。</summary>
public sealed class DataStoreException : Exception
{
    public string Code { get; }

    public DataStoreException(string code, string message, Exception? innerException = null)
        : base(message, innerException) => Code = code;
}
