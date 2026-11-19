using Configurator.Infrastructure;
using Xunit;

namespace Configurator.Security.Tests;

public sealed class UserErrorMessageTests
{
    [Fact]
    public void KeepsRussianValidationMessage()
    {
        Assert.Equal("Поле обязательно для заполнения.", UserErrorMessage.Format(new InvalidOperationException("Поле обязательно для заполнения.")));
    }

    [Theory]
    [InlineData("Cannot insert duplicate key row")]
    [InlineData("Ошибка: Cannot insert duplicate key row")]
    public void DoesNotExposeEnglishExceptionText(string message)
    {
        Assert.Equal("Не удалось выполнить операцию.", UserErrorMessage.Format(new Exception(message)));
    }

    [Fact]
    public void DescribesMissingFileInRussian()
    {
        Assert.Equal("Не удалось найти файл. Проверьте путь и повторите попытку.", UserErrorMessage.Format(new FileNotFoundException("File not found")));
    }
}
