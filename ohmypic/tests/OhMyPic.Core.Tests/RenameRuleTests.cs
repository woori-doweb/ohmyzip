namespace OhMyPic.Core.Tests;

public sealed class RenameRuleTests
{
    private static readonly DateTime Date = new(2026, 11, 5, 13, 0, 0);

    [Fact]
    public void OP_E12_Number_Token_StartNumber_And_Digits()
    {
        Assert.Equal("1", new RenameRule().Apply("x", 0, Date));
        Assert.Equal("img_005", new RenameRule("img_[%N]", StartNumber: 5, Digits: 3).Apply("x", 0, Date));
        Assert.Equal("img_012", new RenameRule("img_[%N]", StartNumber: 5, Digits: 3).Apply("x", 7, Date));
        Assert.Equal("img_1234", new RenameRule("img_[%N]", StartNumber: 1234, Digits: 2).Apply("x", 0, Date));
        Assert.Equal("0", new RenameRule("[%N]", StartNumber: 0).Apply("x", 0, Date));
    }

    [Fact]
    public void OP_E12_Original_Name_And_Date_Tokens()
    {
        Assert.Equal("holiday-20261105-2", new RenameRule("[%F]-[%D]-[%N]").Apply("holiday", 1, Date));
        Assert.Equal("holiday_holiday", new RenameRule("[%f]_[%F]").Apply("holiday", 0, Date));
        Assert.Equal("fixed", new RenameRule("fixed").Apply("holiday", 3, Date));
    }

    [Fact]
    public void OP_E12_Case_And_Find_Replace()
    {
        Assert.Equal("photo_1", new RenameRule("[%F]_[%N]", Case: NameCase.Lower).Apply("PHOTO", 0, Date));
        Assert.Equal("PHOTO_1", new RenameRule("[%F]_[%N]", Case: NameCase.Upper).Apply("photo", 0, Date));
        Assert.Equal("Trip 2026", new RenameRule("[%F]", Find: "_", Replace: " ").Apply("Trip_2026", 0, Date));
        Assert.Equal("ab", new RenameRule("[%F]", Find: "-").Apply("a-b", 0, Date));
        // Find/replace runs before the case change.
        Assert.Equal("NEW-1", new RenameRule("old-[%N]", Case: NameCase.Upper, Find: "old", Replace: "new").Apply("x", 0, Date));
    }

    [Theory]
    [InlineData("a/b")]
    [InlineData("a:b")]
    [InlineData("a*b")]
    [InlineData("a?b")]
    [InlineData("a<b>")]
    [InlineData("a|b")]
    [InlineData("a\"b")]
    [InlineData("")]
    [InlineData("name.")]
    [InlineData("CON")]
    public void OP_E12_Invalid_Characters_Rejected(string template)
    {
        Assert.Throws<ArgumentException>(() => new RenameRule(template).Apply("x", 0, Date));
    }

    [Fact]
    public void OP_E12_Replacement_Introducing_Invalid_Char_Rejected()
    {
        Assert.Throws<ArgumentException>(() => new RenameRule("[%F]", Find: "x", Replace: "/").Apply("xy", 0, Date));
        Assert.Throws<ArgumentException>(() => new RenameRule("[%F]").Apply("a\\b", 0, Date));
    }
}
