using System.Diagnostics;

namespace OhMyZip.Core;

/// <summary>암호 복구의 문자 집합·길이·시간·시도 한도.</summary>
public sealed record PasswordRecoveryOptions(
    string Charset,
    int MinLength,
    int MaxLength,
    long MaxAttempts,
    TimeSpan MaxDuration,
    IReadOnlyList<string>? Dictionary = null);

/// <summary>암호 복구 결과. Password 는 찾았을 때만 메모리에 돌려준다.</summary>
public sealed record PasswordRecoveryResult(
    OperationResult Result,
    string? Password,
    long Attempts,
    TimeSpan Elapsed);

/// <summary>암호화된 압축 파일의 제한된 사전·전수 검색.</summary>
public static class PasswordRecovery
{
    private const int MaxPasswordLength = 8;
    private const long MaxAttemptLimit = 10_000_000;
    private static readonly TimeSpan MaxDurationLimit = TimeSpan.FromHours(1);

    /// <summary>
    /// 압축 파일이 실제로 암호화되어 있는지 먼저 확인한 뒤 후보를 검사한다.
    /// 후보 문자열은 결과·진행 정보·예외에 넣지 않는다.
    /// </summary>
    public static PasswordRecoveryResult Recover(
        string archivePath,
        PasswordRecoveryOptions options,
        IProgress<ProgressInfo>? progress = null,
        CancellationToken cancel = default)
    {
        var stopwatch = Stopwatch.StartNew();
        if (cancel.IsCancellationRequested)
            return Complete(OperationResult.Cancelled(), null, 0, stopwatch);

        OperationResult? validation = ValidateOptions(options, stopwatch, cancel, out string charset);
        if (validation is not null)
            return Complete(validation, null, 0, stopwatch);

        if (string.IsNullOrWhiteSpace(archivePath))
            return Complete(OperationResult.Fail("압축 파일 경로가 올바르지 않습니다"), null, 0, stopwatch);

        TimeSpan remaining = options.MaxDuration - stopwatch.Elapsed;
        if (remaining <= TimeSpan.Zero)
            return Complete(OperationResult.Fail("한도 안에서 찾지 못함 (시도 0회)"), null, 0, stopwatch);
        using var budget = CancellationTokenSource.CreateLinkedTokenSource(cancel);
        ScheduleDeadline(budget, remaining);

        OperationResult probe;
        try
        {
            // 검색 시간 한도는 초기 암호 여부 확인에도 동일하게 적용한다.
            probe = Archives.For(archivePath, new ReadOptions()).Test(archivePath, null, budget.Token);
        }
        catch (OperationCanceledException)
        {
            probe = OperationResult.Cancelled();
        }
        catch
        {
            probe = OperationResult.Fail("압축 파일을 검사할 수 없습니다");
        }

        if (cancel.IsCancellationRequested)
            return Complete(OperationResult.Cancelled(), null, 0, stopwatch);
        if (budget.IsCancellationRequested || stopwatch.Elapsed >= options.MaxDuration)
            return Complete(OperationResult.Fail("한도 안에서 찾지 못함 (시도 0회)"), null, 0, stopwatch);

        if (probe.Status == OperationStatus.Cancelled)
        {
            return Complete(probe, null, 0, stopwatch);
        }
        if (probe.Status == OperationStatus.Succeeded)
            return Complete(OperationResult.Fail("암호가 걸린 압축 파일이 아닙니다"), null, 0, stopwatch);
        if (probe.Status != OperationStatus.PasswordRequired)
            return Complete(probe, null, 0, stopwatch);

        return SearchCore(
            options,
            (candidate, token) =>
            {
                try
                {
                    OperationResult result = Archives.For(archivePath, new ReadOptions { Password = candidate })
                        .Test(archivePath, null, token);
                    return new CandidateCheck(result.Status, result.Message, result.Problems);
                }
                catch
                {
                    // 후보나 예외 텍스트가 외부로 유출되지 않도록 일반 실패로 바꾼다.
                    return new CandidateCheck(OperationStatus.Failed, "암호 후보를 검사할 수 없습니다", []);
                }
            },
            progress,
            cancel,
            budget.Token,
            stopwatch,
            charset);
    }

    /// <summary>
    /// 검색 루프의 내부 진입점. 시험은 압축 엔진 대신 합성 검사기를 주입할 수 있다.
    /// </summary>
    internal static PasswordRecoveryResult Recover(
        PasswordRecoveryOptions options,
        Func<string, CancellationToken, OperationStatus> check,
        IProgress<ProgressInfo>? progress = null,
        CancellationToken cancel = default)
    {
        var stopwatch = Stopwatch.StartNew();
        if (cancel.IsCancellationRequested)
            return Complete(OperationResult.Cancelled(), null, 0, stopwatch);
        if (check is null)
            return Complete(OperationResult.Fail("암호 검사기가 올바르지 않습니다"), null, 0, stopwatch);

        OperationResult? validation = ValidateOptions(options, stopwatch, cancel, out string charset);
        if (validation is not null)
            return Complete(validation, null, 0, stopwatch);

        TimeSpan remaining = options.MaxDuration - stopwatch.Elapsed;
        if (remaining <= TimeSpan.Zero)
            return Complete(OperationResult.Fail("한도 안에서 찾지 못함 (시도 0회)"), null, 0, stopwatch);

        using var budget = CancellationTokenSource.CreateLinkedTokenSource(cancel);
        ScheduleDeadline(budget, remaining);
        return SearchCore(
            options,
            (candidate, token) =>
            {
                OperationStatus status = check(candidate, token);
                return new CandidateCheck(status, "암호 후보를 검사할 수 없습니다", []);
            },
            progress,
            cancel,
            budget.Token,
            stopwatch,
            charset);
    }

    private static PasswordRecoveryResult SearchCore(
        PasswordRecoveryOptions options,
        Func<string, CancellationToken, CandidateCheck> check,
        IProgress<ProgressInfo>? progress,
        CancellationToken cancel,
        CancellationToken budgetToken,
        Stopwatch stopwatch,
        string charset)
    {
        long planned = PlannedAttempts(options, charset.Length);
        long attempts = 0;

        PasswordRecoveryResult LimitResult() =>
            Complete(
                OperationResult.Fail($"한도 안에서 찾지 못함 (시도 {attempts}회)"),
                null,
                attempts,
                stopwatch);

        PasswordRecoveryResult CancelResult() =>
            Complete(OperationResult.Cancelled(), null, attempts, stopwatch);

        bool BeforeCandidate(out PasswordRecoveryResult? terminal)
        {
            if (cancel.IsCancellationRequested)
            {
                terminal = CancelResult();
                return false;
            }
            if (budgetToken.IsCancellationRequested || stopwatch.Elapsed >= options.MaxDuration)
            {
                terminal = LimitResult();
                return false;
            }
            if (attempts >= options.MaxAttempts)
            {
                terminal = LimitResult();
                return false;
            }
            terminal = null;
            return true;
        }

        PasswordRecoveryResult? terminalResult = null;
        bool CheckCandidate(string candidate)
        {
            attempts++;
            CandidateCheck candidateCheck;
            try
            {
                // 반드시 링크된 토큰을 검사기에 전달해 시간 한도가 후보 검사 안에서도 적용되게 한다.
                candidateCheck = check(candidate, budgetToken);
            }
            catch (OperationCanceledException)
            {
                candidateCheck = new CandidateCheck(OperationStatus.Cancelled, "취소됨", []);
            }
            catch
            {
                terminalResult = Complete(
                    OperationResult.Fail("암호 검사 중 오류가 발생했습니다"),
                    null,
                    attempts,
                    stopwatch);
                return false;
            }

            progress?.Report(new ProgressInfo(attempts, planned, null));

            if (cancel.IsCancellationRequested)
            {
                terminalResult = CancelResult();
                return false;
            }
            if (budgetToken.IsCancellationRequested || stopwatch.Elapsed >= options.MaxDuration)
            {
                terminalResult = LimitResult();
                return false;
            }

            if (candidateCheck.Status == OperationStatus.Succeeded)
            {
                terminalResult = Complete(OperationResult.Ok("암호를 찾았습니다"), candidate, attempts, stopwatch);
                return false;
            }
            if (candidateCheck.Status == OperationStatus.PasswordRequired)
                return true;
            if (candidateCheck.Status == OperationStatus.Cancelled)
            {
                terminalResult = CancelResult();
                return false;
            }

            // PasswordRequired 이외의 엔진 상태는 검색을 계속할 수 없는 상태다.
            terminalResult = Complete(
                new OperationResult(
                    candidateCheck.Status,
                    string.IsNullOrWhiteSpace(candidateCheck.Message)
                        ? "암호 후보를 검사할 수 없습니다"
                        : candidateCheck.Message,
                    candidateCheck.Problems),
                null,
                attempts,
                stopwatch);
            return false;
        }

        bool TryCandidate(string candidate)
        {
            if (!BeforeCandidate(out terminalResult))
                return false;
            return CheckCandidate(candidate);
        }

        bool TryDictionaryCandidate(IReadOnlyList<string> dictionary, int index)
        {
            if (!BeforeCandidate(out terminalResult))
                return false;

            string? word;
            try
            {
                word = dictionary[index];
            }
            catch
            {
                terminalResult = Complete(
                    OperationResult.Fail("사전 항목을 읽을 수 없습니다"),
                    null,
                    attempts,
                    stopwatch);
                return false;
            }

            if (word is null)
            {
                terminalResult = Complete(
                    OperationResult.Fail("사전 항목이 올바르지 않습니다"),
                    null,
                    attempts,
                    stopwatch);
                return false;
            }
            return CheckCandidate(word);
        }

        if (options.Dictionary is not null)
        {
            int count;
            try
            {
                count = options.Dictionary.Count;
            }
            catch
            {
                terminalResult = Complete(
                    OperationResult.Fail("사전 항목을 읽을 수 없습니다"),
                    null,
                    attempts,
                    stopwatch);
                count = 0;
            }
            for (int index = 0; index < count && terminalResult is null; index++)
            {
                if (!TryDictionaryCandidate(options.Dictionary, index))
                    break;
            }
        }

        if (terminalResult is not null)
            return terminalResult;

        for (int length = options.MinLength; length <= options.MaxLength; length++)
        {
            char[] candidate = new char[length];
            if (!Enumerate(charset, candidate, 0, TryCandidate))
                break;
        }

        return terminalResult
            ?? LimitResult();
    }

    private readonly record struct CandidateCheck(
        OperationStatus Status,
        string Message,
        IReadOnlyList<string> Problems);

    private static bool Enumerate(
        string charset,
        char[] candidate,
        int index,
        Func<string, bool> tryCandidate)
    {
        if (index == candidate.Length)
            return tryCandidate(new string(candidate));

        foreach (char c in charset)
        {
            candidate[index] = c;
            if (!Enumerate(charset, candidate, index + 1, tryCandidate))
                return false;
        }
        return true;
    }

    private static OperationResult? ValidateOptions(
        PasswordRecoveryOptions? options,
        Stopwatch stopwatch,
        CancellationToken cancel,
        out string charset)
    {
        charset = string.Empty;
        if (cancel.IsCancellationRequested)
            return OperationResult.Cancelled();
        if (options is null)
            return OperationResult.Fail("암호 복구 옵션이 없습니다");
        if (string.IsNullOrEmpty(options.Charset))
            return OperationResult.Fail("문자 집합이 비어 있습니다");
        if (options.MinLength < 0 || options.MaxLength < options.MinLength || options.MaxLength > MaxPasswordLength)
            return OperationResult.Fail("암호 길이 한도가 올바르지 않습니다");
        if (options.MaxAttempts <= 0 || options.MaxAttempts > MaxAttemptLimit)
            return OperationResult.Fail("시도 횟수 한도가 올바르지 않습니다");
        if (options.MaxDuration <= TimeSpan.Zero || options.MaxDuration > MaxDurationLimit)
            return OperationResult.Fail("시간 한도가 올바르지 않습니다");

        return BuildDistinctCharset(options.Charset, stopwatch, cancel, options.MaxDuration, out charset);
    }

    private static OperationResult? BuildDistinctCharset(
        string source,
        Stopwatch stopwatch,
        CancellationToken cancel,
        TimeSpan maxDuration,
        out string charset)
    {
        var distinct = new char[100];
        int count = 0;
        for (int i = 0; i < source.Length; i++)
        {
            if ((i & 0xFF) == 0)
            {
                OperationResult? stop = CheckValidationBudget(stopwatch, cancel, maxDuration);
                if (stop is not null)
                {
                    charset = string.Empty;
                    return stop;
                }
            }

            char current = source[i];
            bool known = false;
            for (int j = 0; j < count; j++)
            {
                if (distinct[j] == current)
                {
                    known = true;
                    break;
                }
            }
            if (known)
                continue;
            if (count == distinct.Length)
            {
                charset = string.Empty;
                return OperationResult.Fail("문자 집합은 서로 다른 문자 100개 이하여야 합니다");
            }
            distinct[count++] = current;
        }

        OperationResult? finalStop = CheckValidationBudget(stopwatch, cancel, maxDuration);
        if (finalStop is not null)
        {
            charset = string.Empty;
            return finalStop;
        }
        charset = new string(distinct, 0, count);
        return null;
    }

    private static long PlannedAttempts(PasswordRecoveryOptions options, int charsetLength)
    {
        long cap = options.MaxAttempts;
        long total = 0;
        if (options.Dictionary is not null)
        {
            int count;
            try
            {
                count = options.Dictionary.Count;
            }
            catch
            {
                count = 0;
            }
            total = Math.Min((long)Math.Max(count, 0), cap);
        }

        long possibilities = 1;
        for (int length = 0; length <= options.MaxLength; length++)
        {
            if (length >= options.MinLength)
            {
                total = SaturatingAdd(total, possibilities, cap);
                if (total == cap)
                    return cap;
            }
            possibilities = SaturatingMultiply(possibilities, charsetLength, cap);
        }
        return Math.Min(total, cap);
    }

    private static OperationResult? CheckValidationBudget(
        Stopwatch stopwatch,
        CancellationToken cancel,
        TimeSpan maxDuration)
    {
        if (cancel.IsCancellationRequested)
            return OperationResult.Cancelled();
        if (stopwatch.Elapsed >= maxDuration)
            return OperationResult.Fail("한도 안에서 찾지 못함 (시도 0회)");
        return null;
    }

    private static long SaturatingAdd(long left, long right, long cap) =>
        left >= cap || right >= cap - left ? cap : left + right;

    private static long SaturatingMultiply(long left, long right, long cap)
    {
        if (left == 0 || right == 0)
            return 0;
        return left > cap / right ? cap : left * right;
    }

    private static PasswordRecoveryResult Complete(
        OperationResult result,
        string? password,
        long attempts,
        Stopwatch stopwatch)
    {
        stopwatch.Stop();
        return new PasswordRecoveryResult(result, password, attempts, stopwatch.Elapsed);
    }

    private static void ScheduleDeadline(CancellationTokenSource source, TimeSpan duration)
    {
        double milliseconds = Math.Ceiling(duration.TotalMilliseconds);
        source.CancelAfter(TimeSpan.FromMilliseconds(Math.Max(1, milliseconds)));
    }
}
