#pragma once
#include "Driver.h"

/// <summary>
/// OS から渡されたスワップチェーンのフレームを引き取り続ける。
/// </summary>
/// <remarks>
/// <para>
/// 間接ディスプレイドライバは、OS が用意したスワップチェーンから
/// フレームを取り出し続ける義務がある。取り出さないでいると、
/// デスクトップの合成は進むのにこちらが処理しない状態が続き、
/// IddCx はしばらくしてドライバを終了させる。
/// 画面が更新されない・仮想ディスプレイが落ちる原因になる。
/// </para>
/// <para>
/// vmonitor では画面の取り込みをアプリ側の Desktop Duplication で行うため、
/// ここで取り出した絵そのものは使わない。パイプラインを回し続けることが目的。
/// </para>
/// <para>
/// 仕様:
/// https://learn.microsoft.com/windows-hardware/drivers/ddi/iddcx/nc-iddcx-evt_idd_cx_monitor_assign_swapchain
/// </para>
/// </remarks>
namespace VMonitorSwapChain
{
    /// <summary>
    /// フレームの引き取りを開始する。
    /// </summary>
    /// <param name="hSwapChain">OS から渡されたスワップチェーン。</param>
    /// <param name="renderAdapterLuid">デスクトップを描いたアダプター。</param>
    /// <param name="hNextSurfaceAvailable">新しい絵が来たときに合図されるイベント。</param>
    /// <returns>
    /// 成功なら true。false のときは呼び出し元が
    /// STATUS_GRAPHICS_INDIRECT_DISPLAY_ABANDON_SWAPCHAIN を返して
    /// OS に作り直させる。
    /// </returns>
    bool Start(IDDCX_SWAPCHAIN hSwapChain, LUID renderAdapterLuid, HANDLE hNextSurfaceAvailable);

    /// <summary>
    /// 引き取りを止める。スレッドが抜けるまで待つ。
    /// </summary>
    void Stop();
}
