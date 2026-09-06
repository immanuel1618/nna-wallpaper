namespace NNA.Wallpaper.Host.Services;

/// <summary>A feature module of the host: registers its routes on the local API.</summary>
public interface IHostService
{
    void Register(LocalApi api);
}
