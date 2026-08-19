namespace QuickPhrase.Core;

/// <summary>
/// 话术命令的纯领域校验。它不访问数据库，也不依赖 Windows，确保所有写入入口都遵守
/// “话术必须有合法分类”的产品规则；分类是否真实存在由 Platform.Windows 在事务内继续确认。
/// </summary>
public static class PhraseRules
{
    public static bool Validate(CreatePhraseCommand command, out DataError? error)
    {
        return Validate(command.Title, command.Content, command.CategoryId, out error);
    }

    public static bool Validate(UpdatePhraseCommand command, out DataError? error)
    {
        return Validate(command.Title, command.Content, command.CategoryId, out error);
    }

    private static bool Validate(string title, string content, Guid categoryId, out DataError? error)
    {
        if (string.IsNullOrWhiteSpace(title))
        {
            error = new DataError("VALIDATION_FAILED", "话术标题不能为空。");
            return false;
        }

        if (string.IsNullOrWhiteSpace(content))
        {
            error = new DataError("VALIDATION_FAILED", "话术正文不能为空。");
            return false;
        }

        if (categoryId == Guid.Empty)
        {
            error = new DataError("VALIDATION_FAILED", "话术必须归属一个分类。");
            return false;
        }

        error = null;
        return true;
    }
}
