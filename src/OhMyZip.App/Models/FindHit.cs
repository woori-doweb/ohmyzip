using System.Text.RegularExpressions;

namespace OhMyZip.App.Models;

/// <summary>찾기 결과 한 줄. Name 은 표시용 전체 경로(UIA 이름도 이 값).</summary>
public sealed record FindHit(string Path, bool IsDirectory)
{
    public string Name => IsDirectory ? Path + "/" : Path;
    public string Glyph => IsDirectory ? "\uE8B7" : "\uE8A5";
    public override string ToString() => Name;
}

/// <summary>
/// 찾기 조건. '*' '?' 가 있으면 와일드카드(이름 전체와 일치), 없으면 부분 문자열. 대소문자 무시.
/// 조건에 '/' 가 있으면 전체 경로, 없으면 이름(마지막 조각)과 비교한다.
/// </summary>
public sealed class FindQuery
{
    private readonly string _text;
    private readonly Regex? _wildcard;
    private readonly bool _matchPath;

    public FindQuery(string text)
    {
        _text = text;
        _matchPath = text.Contains('/');
        if (text.IndexOfAny(['*', '?']) >= 0)
        {
            var pattern = "^" + Regex.Escape(text).Replace(@"\*", ".*").Replace(@"\?", ".") + "$";
            _wildcard = new Regex(pattern, RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(200));
        }
    }

    public bool IsMatch(string path)
    {
        var subject = _matchPath ? path : ArchiveModel.LeafName(path);
        try
        {
            return _wildcard is not null
                ? _wildcard.IsMatch(subject)
                : subject.Contains(_text, StringComparison.CurrentCultureIgnoreCase);
        }
        catch (RegexMatchTimeoutException)
        {
            return false;
        }
    }
}
