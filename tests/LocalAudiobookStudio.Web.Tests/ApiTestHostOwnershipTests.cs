using System.Net;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;

namespace LocalAudiobookStudio.Web.Tests;

public sealed class ApiTestHostOwnershipTests
{
    [Fact]
    public async Task Disposing_host_that_borrows_root_preserves_owner_project_and_directory()
    {
        // Isolate ownership from polling: a borrower deleting this root is a
        // deterministic defect even when no background reader holds catalog.lock.
        await using var owner = await ApiTestHost.StartAsync(configureServices: WithoutDispatch);
        var project = await owner.CreateAsync();
        var borrower = await ApiTestHost.StartAsync(root: owner.Root, configureServices: WithoutDispatch, deleteRootOnDispose: false);
        await borrower.DisposeAsync();
        Assert.True(Directory.Exists(owner.Root));
        using var current = await owner.SendAsync(HttpMethod.Get, $"/api/projects/{project.Id}");
        Assert.Equal(HttpStatusCode.OK, current.StatusCode);
        Assert.Equal(project.Id, (await ApiTestHost.JsonAsync(current)).GetProperty("id").GetString());
    }
    private static void WithoutDispatch(IServiceCollection services) => services.RemoveAll<IHostedService>();
}
