using Xunit;

namespace KmTronicRelay.Tests;

/**
 * @brief Unit tests for settings normalization and FTDI hardware ID parsing helpers.
 */
public sealed class SettingsNormalizationTests {
    /**
     * @brief Verifies COM port numeric sorting extracts the port number suffix.
     */
    [Theory]
    [InlineData("COM3", 3)]
    [InlineData("com12", 12)]
    [InlineData("COM", int.MaxValue)]
    [InlineData("DEVICE", int.MaxValue)]
    public void ParseComPortNumber_extracts_numeric_suffix(string portName, int expected) {
        Assert.Equal(expected, KmTronicCommands.ParseComPortNumber(portName));
    }

    /**
     * @brief Verifies blank and N/A COM assignments normalize to NotApplicable.
     */
    [Theory]
    [InlineData(null, KmTronicCommands.NotApplicable)]
    [InlineData("", KmTronicCommands.NotApplicable)]
    [InlineData("N/A", KmTronicCommands.NotApplicable)]
    [InlineData("COM5", "COM5")]
    public void NormalizeChannelComPort_maps_blank_values(string? input, string expected) {
        Assert.Equal(expected, KmTronicCommands.NormalizeChannelComPort(input));
    }

    /**
     * @brief Verifies invalid serial settings are clamped to safe defaults.
     */
    [Fact]
    public void NormalizeSerialSettings_clamps_invalid_baud_and_clears_empty_log_path() {
        SerialPortSettings normalized = KmTronicCommands.NormalizeSerialSettings(new SerialPortSettings {
            BaudRate = 50,
            DataBits = 3,
            StopBits = "Invalid",
            Parity = "Invalid",
            Handshake = "Invalid",
            LogToFile = true,
            LogFilePath = "   "
        });

        Assert.Equal(115200, normalized.BaudRate);
        Assert.Equal(8, normalized.DataBits);
        Assert.Equal("One", normalized.StopBits);
        Assert.Equal("None", normalized.Parity);
        Assert.Equal("None", normalized.Handshake);
        Assert.False(normalized.LogToFile);
    }

    /**
     * @brief Verifies FTDI KMTronic VID/PID matching is case-insensitive.
     */
    [Theory]
    [InlineData("USB\\VID_0403&PID_6001\\ABC", true)]
    [InlineData("vid_0403+pid_6001", true)]
    [InlineData("USB\\VID_0403&PID_6002\\ABC", false)]
    [InlineData("", false)]
    public void ContainsFtdiIds_matches_kmtronic_hardware_ids(string hardwareId, bool expected) {
        Assert.Equal(expected, KmTronicCommands.ContainsFtdiIds(hardwareId));
    }

    /**
     * @brief Verifies log timestamp formatting is stable and includes millisecond precision.
     */
    [Fact]
    public void FormatLogTimestamp_uses_iso_like_prefix() {
        DateTime timestamp = new(2026, 7, 5, 9, 40, 12, 345);
        Assert.Equal("[2026-07-05 09:40:12.345] ", SerialPortSettings.FormatLogTimestamp(timestamp));
    }
}
