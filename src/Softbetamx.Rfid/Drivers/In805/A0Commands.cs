using System.Globalization;

namespace Softbetamx.Rfid.Drivers.In805;

/// <summary>Códigos de comando, códigos de error y decodificación de respuestas del protocolo 0xA0.</summary>
internal static class A0Commands
{
    public const byte GetFirmware = 0x72;
    public const byte SetWorkAntenna = 0x74;
    public const byte GetOutputPower = 0x77;
    public const byte GetRegion = 0x79;
    public const byte SetBeeperMode = 0x7A;
    public const byte GetTemperature = 0x7B;
    public const byte GetReturnLoss = 0x7E;
    /// <summary>Potencia temporal (no se guarda en flash).</summary>
    public const byte SetTemporaryPower = 0x66;
    public const byte CustomizedSessionTargetInventory = 0x8B;
    public const byte ReadGpio = 0x60;
    public const byte WriteGpio = 0x61;

    public const byte Success = 0x10;
    public const byte AntennaMissing = 0x22;

    /// <summary>Parámetro de frecuencia de 0x7E para 915 MHz (902 + (0x21 - 7) * 0.5).</summary>
    public const byte ReturnLossFreq915 = 0x21;

    private static readonly Dictionary<byte, string> Errors = new()
    {
        [0x10] = "command_success", [0x11] = "command_fail", [0x20] = "mcu_reset_error", [0x21] = "cw_on_error",
        [0x22] = "antenna_missing", [0x23] = "write_flash_error", [0x24] = "read_flash_error",
        [0x25] = "set_output_power_error", [0x31] = "tag_inventory_error", [0x32] = "tag_read_error",
        [0x33] = "tag_write_error", [0x34] = "tag_lock_error", [0x35] = "tag_kill_error", [0x36] = "no_tag",
        [0x37] = "inventory_ok_but_access_fail", [0x38] = "buffer_empty", [0x3C] = "nxp_custom_fail",
        [0x40] = "access_or_password_error", [0x41] = "parameter_invalid", [0x42] = "wordcnt_too_long",
        [0x43] = "membank_out_of_range", [0x44] = "lock_region_out_of_range", [0x45] = "lock_action_out_of_range",
        [0x46] = "reader_address_invalid", [0x47] = "antenna_id_out_of_range", [0x48] = "output_power_out_of_range",
        [0x49] = "frequency_region_out_of_range", [0x4A] = "baudrate_out_of_range", [0x4B] = "beeper_mode_out_of_range",
    };

    public static string ErrorName(byte code) =>
        $"0x{code:X2} {(Errors.TryGetValue(code, out var name) ? name : "unknown")}";

    /// <summary>0x72 → "major.minor" con minor a 2 dígitos (ej. 81 01 → "129.01").</summary>
    public static string ParseFirmware(byte[] data)
    {
        Require(data, 2, GetFirmware);
        return $"{data[0]}.{data[1]:D2}";
    }

    /// <summary>0x79 → [región, inicio, fin]. FCC: MHz = 902 + (param - 7) * 0.5.</summary>
    public static string ParseRegion(byte[] data)
    {
        Require(data, 3, GetRegion);
        byte region = data[0], start = data[1], end = data[2];
        return region switch
        {
            0x01 => $"FCC {Mhz(902 + (start - 7) * 0.5)}-{Mhz(902 + (end - 7) * 0.5)} MHz",
            // Solo FCC está verificada en el equipo; las demás se reportan con sus parámetros crudos.
            0x02 => $"ETSI (0x{start:X2}-0x{end:X2})",
            0x03 => $"CHN (0x{start:X2}-0x{end:X2})",
            _ => $"Region 0x{region:X2} (0x{start:X2}-0x{end:X2})",
        };
    }

    /// <summary>0x77 → 1 valor (todas las antenas) o 4 valores (uno por antena), en dBm.</summary>
    public static IReadOnlyList<int> ParsePower(byte[] data)
    {
        if (data.Length != 1 && data.Length != 4)
            throw new RfidReaderException($"Unexpected response to 0x{GetOutputPower:X2}: {Convert.ToHexString(data)}");
        return data.Select(b => (int)b).ToArray();
    }

    /// <summary>0x7B → [signo (1 = +, 0 = -), grados].</summary>
    public static int ParseTemperature(byte[] data)
    {
        Require(data, 2, GetTemperature);
        return data[0] == 0 ? -data[1] : data[1];
    }

    /// <summary>RSSI en dBm. El bit alto (banco de antenas 5-8) se ignora en un lector de 4 puertos.</summary>
    public static int RssiDbm(byte raw) => (raw & 0x7F) - 129;

    /// <summary>
    /// Clasifica un paquete de 0x8B (o 0x89/0x8A): tag, resumen (Len 0x0A) o error (Len 0x04).
    /// El paquete de tag se reconoce porque su longitud cuadra con el EPC indicado por el PC.
    /// </summary>
    public static InventoryPacket ParseInventoryPacket(A0Frame frame)
    {
        var d = frame.Data;
        if (TryParseTag(d, out var tag)) return tag;
        if (frame.Length == 0x0A && d.Length == 7)
        {
            return new InventorySummary(
                AntennaId: d[0],
                ReadRate: (d[1] << 8) | d[2],
                TotalRead: (int)(((uint)d[3] << 24) | ((uint)d[4] << 16) | ((uint)d[5] << 8) | d[6]));
        }
        if (frame.Length == 0x04 && d.Length == 1) return new InventoryError(d[0]);
        return new InventoryUnknown(d);
    }

    private static bool TryParseTag(byte[] d, out InventoryTag tag)
    {
        tag = null!;
        if (d.Length < 4) return false;
        int pc = (d[1] << 8) | d[2];
        int epcLen = ((pc >> 11) & 0x1F) * 2;
        int rest = d.Length - 3 - epcLen;
        if (rest != 1 && rest != 3) return false; // 3 = con fase (2 bytes) al final
        tag = new InventoryTag(
            Antenna: (d[0] & 0x03) + 1,
            Epc: Convert.ToHexString(d, 3, epcLen),
            Rssi: RssiDbm(d[3 + epcLen]),
            Pc: pc);
        return true;
    }

    private static void Require(byte[] data, int length, byte cmd)
    {
        if (data.Length != length)
            throw new RfidReaderException(
                data.Length == 1
                    ? $"Command 0x{cmd:X2} failed: {ErrorName(data[0])}"
                    : $"Unexpected response to 0x{cmd:X2}: {Convert.ToHexString(data)}",
                data.Length == 1 ? data[0] : null);
    }

    private static string Mhz(double value) => value.ToString("0.###", CultureInfo.InvariantCulture);
}

internal abstract record InventoryPacket;

/// <param name="Antenna">Antena 1..4.</param>
/// <param name="Epc">EPC en hex mayúsculas.</param>
/// <param name="Rssi">RSSI en dBm.</param>
internal sealed record InventoryTag(int Antenna, string Epc, int Rssi, int Pc) : InventoryPacket;

/// <param name="AntennaId">Antena (base 0) reportada en el resumen.</param>
internal sealed record InventorySummary(int AntennaId, int ReadRate, int TotalRead) : InventoryPacket;

internal sealed record InventoryError(byte Code) : InventoryPacket;

internal sealed record InventoryUnknown(byte[] Data) : InventoryPacket;
