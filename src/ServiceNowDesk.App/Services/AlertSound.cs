using System.IO;
using System.Media;

namespace ServiceNowDesk.Services;

public sealed class AlertSound
{
    private SoundPlayer? _player;

    public void Play(string? path)
    {
        if (TryPlayFile(path))
            return;

        try
        {
            SystemSounds.Exclamation.Play();
        }
        catch
        {
        }
    }

    private bool TryPlayFile(string? path)
    {
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
            return false;

        try
        {
            _player ??= new SoundPlayer();
            if (!string.Equals(_player.SoundLocation, path, StringComparison.OrdinalIgnoreCase))
                _player.SoundLocation = path;

            _player.Play();
            return true;
        }
        catch
        {
            return false;
        }
    }
}
