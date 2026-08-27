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

namespace Mame4droid.Lobby.Endpoints;

public static class NoStoreExtensions
{
    /// Marks a GET whose answer must never come from a cache. None of them is
    /// worth storing and a stale one misleads: config decides what the client
    /// does, and whoami is about whoever is asking. The room list is the one
    /// GET left out on purpose -- its ETag and 304s are what keep the board
    /// cheap to watch.
    public static RouteHandlerBuilder NoStore(this RouteHandlerBuilder builder) =>
        builder.AddEndpointFilter(async (ctx, next) =>
        {
            ctx.HttpContext.Response.Headers.CacheControl = "no-store";
            return await next(ctx);
        });
}
