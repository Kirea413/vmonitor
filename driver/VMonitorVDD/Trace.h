#pragma once
#include <windows.h>

//
// ドライバの初期化がどこで失敗したかを追うための最小限のトレース。
//
// UMDF ドライバは WUDFHost.exe の中で動くため、標準出力もデバッガーも
// そのままでは使えない。デバイスが CM_PROB_FAILED_ADD になったときに
// 「どの呼び出しが、どのステータスで失敗したか」が分からないと、
// 原因の切り分けが当てずっぽうになる。
//
// 出力先は C:\ProgramData\vmonitor-driver.log。
//
// %SystemRoot%\Temp は WUDFHost からは書けても、調べる側のユーザーが
// 読めないことがある（ディレクトリの一覧が拒否される）。
// 「ログが無い」のか「見えないだけ」なのか区別が付かなくなるので、
// 双方が読み書きできる ProgramData に置く。
//

namespace VMonitorTrace
{
    inline void Write(const char* message, long status = 0, bool hasStatus = false)
    {
        HANDLE file = CreateFileW(
            L"C:\\ProgramData\\vmonitor-driver.log",
            FILE_APPEND_DATA,
            FILE_SHARE_READ | FILE_SHARE_WRITE,
            nullptr,
            OPEN_ALWAYS,
            FILE_ATTRIBUTE_NORMAL,
            nullptr);

        if (file == INVALID_HANDLE_VALUE)
            return;

        char buffer[256];
        int  length = 0;

        // 時刻を先頭に付ける
        SYSTEMTIME now;
        GetLocalTime(&now);

        auto AppendChar = [&](char c)
        {
            if (length < (int)sizeof(buffer) - 1) buffer[length++] = c;
        };

        auto AppendNumber = [&](unsigned long value, int digits)
        {
            char tmp[16];
            int  n = 0;

            do { tmp[n++] = (char)('0' + (value % 10)); value /= 10; } while (value && n < 16);
            while (n < digits) tmp[n++] = '0';
            while (n > 0) AppendChar(tmp[--n]);
        };

        AppendNumber(now.wHour, 2);   AppendChar(':');
        AppendNumber(now.wMinute, 2); AppendChar(':');
        AppendNumber(now.wSecond, 2); AppendChar(' ');

        for (const char* p = message; *p; ++p) AppendChar(*p);

        if (hasStatus)
        {
            const char* prefix = " status=0x";
            for (const char* p = prefix; *p; ++p) AppendChar(*p);

            // 32 ビットを 16 進 8 桁で出す
            for (int shift = 28; shift >= 0; shift -= 4)
            {
                int nibble = (int)((((unsigned long)status) >> shift) & 0xF);
                AppendChar(nibble < 10 ? (char)('0' + nibble) : (char)('A' + nibble - 10));
            }
        }

        AppendChar('\r');
        AppendChar('\n');

        DWORD written = 0;
        WriteFile(file, buffer, (DWORD)length, &written, nullptr);
        CloseHandle(file);
    }

    inline void WriteStatus(const char* message, long status)
    {
        Write(message, status, true);
    }
}

#define VMTRACE(msg)               VMonitorTrace::Write(msg)
#define VMTRACE_STATUS(msg, st)    VMonitorTrace::WriteStatus(msg, (long)(st))
