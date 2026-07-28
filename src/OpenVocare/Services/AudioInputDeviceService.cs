using Windows.Devices.Enumeration;
using Windows.Media.Devices;

namespace OpenVocare.Services;

public sealed record AudioInputDevice(string Id, string Name);

public static class AudioInputDeviceService
{
    public static async Task<IReadOnlyList<AudioInputDevice>> GetDevicesAsync(
        CancellationToken cancellationToken = default)
    {
        DeviceInformationCollection devices = await DeviceInformation.FindAllAsync(
            MediaDevice.GetAudioCaptureSelector()).AsTask(cancellationToken);

        return devices
            .Where(device => device.IsEnabled && !string.IsNullOrWhiteSpace(device.Id))
            .Select(device => new AudioInputDevice(device.Id, device.Name))
            .DistinctBy(device => device.Id, StringComparer.OrdinalIgnoreCase)
            .OrderBy(device => device.Name, StringComparer.CurrentCultureIgnoreCase)
            .ToArray();
    }
}
