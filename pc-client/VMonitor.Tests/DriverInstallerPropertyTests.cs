using FsCheck;
using FsCheck.Xunit;
using VMonitor.Driver;

namespace VMonitor.Tests;

// Feature: vmonitor, Property 1: エラーメッセージの表示・非表示の正確さ

/// <summary>
/// Property 1: エラーメッセージの表示・非表示の正確さ
/// Validates: Requirements 1.5
///
/// 任意のドライバインストール失敗エラーコードに対して、インストーラーが生成する
/// エラーメッセージは非空かつ対処手順テキストを含まなければならない。
/// また、インストール成功時にはエラーメッセージが空文字列でなければならない。
/// </summary>
public class DriverInstallerPropertyTests
{
    /// <summary>
    /// Property 1a: 任意の失敗エラーコードに対してエラーメッセージは非空でなければならない。
    /// NonZeroInt は FsCheck が提供する「0 以外の整数」ラッパーで、失敗コードを表す。
    /// </summary>
    [Property]
    public bool ErrorMessageIsNonEmptyForAnyFailureExitCode(NonZeroInt exitCodeWrapper, string? stderrNullable)
    {
        var exitCode = exitCodeWrapper.Get;
        var stderr = stderrNullable ?? string.Empty;
        var message = DriverInstaller.GenerateErrorMessage(exitCode, stderr);
        return !string.IsNullOrEmpty(message);
    }

    /// <summary>
    /// Property 1b: 任意の失敗エラーコードに対してエラーメッセージは対処手順テキストを含まなければならない。
    /// </summary>
    [Property]
    public bool ErrorMessageContainsRemediationForAnyFailureExitCode(NonZeroInt exitCodeWrapper, string? stderrNullable)
    {
        var exitCode = exitCodeWrapper.Get;
        var stderr = stderrNullable ?? string.Empty;
        var message = DriverInstaller.GenerateErrorMessage(exitCode, stderr);
        return message.Contains("対処手順");
    }

    /// <summary>
    /// Property 1c: インストール成功時（exitCode == 0）にはエラーメッセージが空文字列でなければならない。
    /// </summary>
    [Property]
    public bool ErrorMessageIsEmptyOnSuccess(string? stderrNullable)
    {
        var stderr = stderrNullable ?? string.Empty;
        var message = DriverInstaller.GenerateErrorMessage(0, stderr);
        return message == string.Empty;
    }
}
