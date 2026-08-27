/*
 * This file is part of MAME4droid (NetPlay lobby server).
 *
 * Copyright (C) 2026 David Valdeita (Seleuco)
 *
 * This program is free software; you can redistribute it and/or modify
 * it under the terms of the GNU General Public License as published by
 * the Free Software Foundation; either version 2 of the License, or
 * (at your option) any later version.
 *
 * This program is distributed in the hope that it will be useful,
 * but WITHOUT ANY WARRANTY; without even the implied warranty of
 * MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE. See the GNU
 * General Public License for more details.
 *
 * You should have received a copy of the GNU General Public License
 * along with this program; if not, see <http://www.gnu.org/licenses>.
 */

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
