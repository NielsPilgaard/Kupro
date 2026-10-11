using Aspire.Hosting.Docker;

internal static class ComposeBindMounts
{
    /// <summary>
    /// `aspire publish` turns every bind-mount source into a *_BINDMOUNT_n variable for the deployer to fill in.
    /// Dokploy deploys from a checkout of this repo, so point them at the files in the checkout instead, relative to
    /// deploy/ where the compose file is written (ADR-012).
    /// </summary>
    public static IResourceBuilder<DockerComposeEnvironmentResource> WithRepoRelativeBindMounts(
        this IResourceBuilder<DockerComposeEnvironmentResource> compose)
    {
        var app = compose.ApplicationBuilder;
        var deployDir = Path.GetFullPath(Path.Combine(app.AppHostDirectory, "..", "..", "deploy"));

        return compose
            .ConfigureComposeFile(file =>
            {
                foreach (var resource in app.Resources)
                {
                    if (!file.Services.TryGetValue(resource.Name, out var service))
                    {
                        continue;
                    }

                    foreach (var mount in resource.Annotations.OfType<ContainerMountAnnotation>()
                                 .Where(m => m.Type == ContainerMountType.BindMount && m.Source is not null))
                    {
                        foreach (var volume in service.Volumes.Where(v => v.Target == mount.Target))
                        {
                            volume.Source = Path.GetRelativePath(deployDir, mount.Source!).Replace('\\', '/');
                        }
                    }
                }
            })
            .ConfigureEnvFile(env =>
            {
                foreach (var key in env.Keys.Where(k => k.Contains("_BINDMOUNT_")).ToList())
                {
                    env.Remove(key);
                }
            });
    }
}
