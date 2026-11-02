namespace Softbetamx.Rfid.Tests.Focus;

/// <summary>
/// Frames captured from the real Focus reader (ST-M714, E710) on 2026-11-01 over TCP 192.168.1.100:8080,
/// and examples printed in the EX10 protocol manual.
/// </summary>
internal static class FocusCaptures
{
    // ---- host → reader (live TX)
    public const string TxStop = "FF 0E AA 4D 6F 64 75 6C 65 74 65 63 68 AA 49 F3 BB 03 91";
    public const string TxVersion = "FF 00 03 1D 0C";
    public const string TxSerial = "FF 00 10 1D 1F";
    public const string TxTemperature = "FF 00 72 1D 7D";
    public const string TxRegion = "FF 00 67 1D 68";
    public const string TxInvAntennas = "FF 01 61 02 BD BF";
    public const string TxPowers = "FF 01 61 03 BD BE";
    public const string TxConnected = "FF 01 61 05 BD B8";
    public const string TxPowerInfo = "FF 01 62 01 BE BC";
    public const string TxGetSession = "FF 02 6B 05 00 3A 6F";
    public const string TxGetTarget = "FF 02 6B 05 01 3A 6E";
    public const string TxGetRfMode = "FF 02 6B 05 02 3A 6D";
    public const string TxGetQ = "FF 02 6B 05 12 3A 7D";
    public const string TxClearBuffer = "FF 00 2A 1D 25";
    public const string TxSyncInventory500 = "FF 05 22 00 00 00 01 F4 09 4B";
    public const string TxTagBuffer3F = "FF 03 29 00 3F 00 CB 22";
    public const string TxSetAntenna1 = "FF 03 91 02 01 01 42 C5";
    public const string TxSetAntennas12 = "FF 05 91 02 01 01 02 02 2D C0";
    public const string TxAsyncStart3FA003 = "FF 13 AA 4D 6F 64 75 6C 65 74 65 63 68 AA 48 00 3F 00 A0 03 D4 BB 12 D1";
    public const string TxReturnLossAnt1 = "FF 13 AA 4D 6F 64 75 6C 65 74 65 63 68 AA 4A 07 D0 01 01 00 CD BB 9E 5D";

    // ---- reader → host (live RX)
    public const string RxStop = "FF 0C AA 00 00 4D 6F 64 75 6C 65 74 65 63 68 AA 49 0F 22";
    public const string RxVersion = "FF 14 03 00 00 22 02 11 00 31 02 00 20 20 25 07 14 25 07 14 00 00 00 00 10 2C 26";
    public const string RxSerial = "FF 0C 10 00 00 00 00 00 30 00 00 26 16 00 00 08 C0 DA 29";
    public const string RxTemperature = "FF 01 72 00 00 1A 48 1D";
    public const string RxRegion = "FF 01 67 00 00 01 B4 80";
    public const string RxInvAntennas12 = "FF 05 61 00 00 02 01 01 02 02 34 1F";
    public const string RxInvAntenna1 = "FF 03 61 00 00 02 01 01 11 39";
    public const string RxPowers = "FF 15 61 00 00 03 01 0C E4 0C E4 02 0C E4 0C E4 03 0C E4 0C E4 04 0C E4 0C E4 4D 5E";
    public const string RxConnected = "FF 09 61 00 00 05 01 01 02 01 03 00 04 00 6B C4";
    public const string RxPowerInfo = "FF 07 62 00 00 01 0C E4 0C E4 00 00 06 EF";
    public const string RxSession = "FF 03 6B 00 00 05 00 00 08 74";
    public const string RxTarget = "FF 04 6B 00 00 05 01 01 00 2C 68";
    public const string RxRfMode = "FF 03 6B 00 00 05 02 6B 0A 1F";
    public const string RxQ = "FF 03 6B 00 00 05 12 00 1A 74";
    public const string RxSetAntennaOk = "FF 00 91 00 00 17 58";
    public const string RxClearBuffer = "FF 00 2A 00 00 01 E8";
    public const string RxSyncInventory4 = "FF 04 22 00 00 00 00 00 04 7B AC";

    public const string RxTagBuffer4 =
        "FF 7C 29 00 00 00 3F 00 04 06 B9 02 0D C8 52 00 00 00 1A 0E 25 00 80 30 00 E2 80 68 94 00 00 40 26 2F E8 A9 D3 0D DC " +
        "09 BE 02 0D C8 52 00 00 00 30 05 C0 00 80 30 00 E2 00 00 17 57 0D 02 07 21 80 35 3E 05 B0 " +
        "02 B9 02 0E 26 12 00 00 00 B1 0A 28 00 80 34 00 00 00 00 00 00 00 00 02 EE 6D 4B 1F 0A 1E " +
        "01 B9 02 0E 26 12 00 00 01 4D 06 6C 00 80 30 00 30 34 02 89 10 57 60 5B 02 92 A1 BF 06 5A 9A E2";

    public const string RxAsyncAck = "FF 0C AA 00 00 4D 6F 64 75 6C 65 74 65 63 68 AA 48 0F 23";
    public const string RxHeartbeatA003 = "FF 06 AA 00 00 58 54 53 4A A0 03 37 24";
    public const string RxAsyncTag1 = "FF 1F AA 00 00 00 3F 01 C0 01 0D E9 86 00 00 00 2C 05 33 10 30 00 E2 80 68 94 00 00 40 26 2F E8 A9 D3 05 2C 51 80";
    public const string RxAsyncTag2 = "FF 1F AA 00 00 00 3F 01 B6 02 0E 10 96 00 00 00 5D 00 88 10 30 00 30 34 02 89 10 57 60 5B 02 92 A1 BF 00 89 76 63";
    public const string RxAsyncRoundEnd = "FF 14 AA 00 00 00 3F 01 00 02 00 00 00 00 00 00 00 00 00 05 00 00 01 00 00 9F 5E";

    public const string RxReturnLossAnt1 =
        "FF D9 AA 00 00 4D 6F 64 75 6C 65 74 65 63 68 AA 4A 07 D0 01 01 00 0D F7 32 AA 0D C6 5E AA 0E 26 12 C8 0D F9 26 AA " +
        "0D C8 52 AA 0E 24 1E C8 0D DB DA AA 0E 02 EA B4 0E 16 72 BE 0D D0 22 AA 0D FB 1A AA 0D E7 92 AA 0E 0E A2 BE 0D EF 62 AA " +
        "0D D4 0A AA 0D F3 4A AA 0D DF C2 AA 0E 06 D2 B4 0E 1A 5A C8 0D CC 3A AA 0D FF 02 AA 0D EB 7A AA 0E 12 8A C8 0E 0A BA BE " +
        "0D D7 F2 AA 0D F5 3E AA 0D E1 B6 AA 0E 08 C6 B4 0E 1C 4E C8 0D CE 2E AA 0D ED 6E AA 0D D9 E6 AA 0E 00 F6 B4 0E 14 7E C8 " +
        "0D E3 AA AA 0E 1E 42 D2 0D E5 9E AA 0E 0C AE B4 0E 20 36 D2 0D D2 16 AA 0D F1 56 AA 0D DD CE AA 0E 04 DE B4 0E 18 66 C8 " +
        "0D CA 46 AA 0D FD 0E AA 0D E9 86 AA 0E 10 96 BE 0E 22 2A C8 0D D5 FE AA 6E 95";

    public const string RxIoGet = "49 4F 47 45 54 0F";

    // ---- manual examples (EX10 protocol manual, 2023-05-30)
    public const string ManualAsyncStartBF = "FF 13 AA 4D 6F 64 75 6C 65 74 65 63 68 AA 48 00 BF 00 80 03 34 BB 29 0F";
    public const string ManualSetSession1 = "FF 03 9B 05 00 01 DC E9";
    public const string ManualTagBuffer15 =
        "FF 34 29 00 00 00 15 00 02 22 01 02 50 CE F6 00 80 31 C1 11 11 22 22 33 33 44 44 55 55 66 66 FB 15 " +
        "0E 01 04 1D 3D 3C 00 80 30 00 05 00 00 00 00 00 00 00 00 00 23 54 4A C8 92 A3";
    public const string ManualTagBufferBF =
        "FF 4A 29 00 00 00 BF 00 02 07 E3 01 0E 22 2A 00 00 8D 8F 00 00 00 00 00 60 20 00 11 11 22 22 33 33 44 44 C2 41 " +
        "07 D0 01 0E 22 2A 00 00 8D 87 00 00 00 00 00 D0 58 00 11 11 22 22 33 33 44 44 55 55 66 66 77 77 88 88 99 99 00 00 AA AA 96 86 FD 4C";
    public const string ManualAsyncTag3F = "FF 1B AA 00 00 00 3F 01 BD 02 0D F7 32 00 00 00 13 00 00 0C 20 00 11 11 20 19 02 11 01 94 22 AF E2 59";
    public const string ManualAsyncTagBF = "FF 21 AA 00 00 00 BF 01 D3 01 0D CC 3A 00 00 00 1A 00 17 00 00 10 30 00 E2 00 00 1D 40 01 01 58 10 40 82 73 36 C1 42 A1";
    public const string ManualHeartbeat = "FF 06 AA 00 00 58 54 53 4A 80 03 17 24";
    public const string ManualRoundEnd = "FF 09 AA 00 00 00 04 02 05 00 00 01 00 00 F5 75";
    public const string ManualInterrupted = "FF 00 03 AA 49 1E EA";
}
