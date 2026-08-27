using Mame4droid.Lobby.Services;
using Xunit;

namespace Mame4droid.Lobby.Tests;

/// The build stamp only earns its keep if it really changes per build and
/// really reaches the page: it is what says whether the package just uploaded
/// is the one answering requests.
public class BuildInfoTests
{
    [Fact]
    public void The_running_build_is_stamped_and_not_a_placeholder()
    {
        Assert.NotEqual("dev", BuildInfo.Version);
        Assert.Contains("+", BuildInfo.Version);
    }

    [Fact]
    public async Task The_home_page_names_the_build_it_is_running()
    {
        using var factory = new LobbyFactory();

        var page = await (await factory.CallerFrom("88.1.2.3").GetAsync("/"))
            .Content.ReadAsStringAsync();

        Assert.Contains(BuildInfo.Version, page);
    }
}
