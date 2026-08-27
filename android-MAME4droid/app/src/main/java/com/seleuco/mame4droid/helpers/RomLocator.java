/*
 * This file is part of MAME4droid.
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
 *
 * Linking MAME4droid statically or dynamically with other modules is
 * making a combined work based on MAME4droid. Thus, the terms and
 * conditions of the GNU General Public License cover the whole
 * combination.
 *
 * In addition, as a special exception, the copyright holders of MAME4droid
 * give you permission to combine MAME4droid with free software programs
 * or libraries that are released under the GNU LGPL and with code included
 * in the standard release of MAME under the MAME License (or modified
 * versions of such code, with unchanged license). You may copy and
 * distribute such a system following the terms of the GNU GPL for MAME4droid
 * and the licenses of the other code concerned, provided that you include
 * the source code of that other code when and as the GNU GPL requires
 * distribution of source code.
 *
 * Note that people who make modified versions of MAME4idroid are not
 * obligated to grant this special exception for their modified versions; it
 * is their choice whether to do so. The GNU General Public License
 * gives permission to release a modified version without this exception;
 * this exception also makes it possible to release a modified version
 * which carries forward this exception.
 *
 * MAME4droid is dual-licensed: Alternatively, you can license MAME4droid
 * under a MAME license, as set out in http://mamedev.org/
 */

package com.seleuco.mame4droid.helpers;

import android.content.Intent;
import android.net.Uri;

import com.seleuco.mame4droid.MAME4droid;

import java.io.File;
import java.util.ArrayList;

/**
 * Answers whether a game's ROM is somewhere this build will look for it.
 * A hit is not a promise the set is complete, only that the game is there.
 * Covers the SAF folder, the internal "roms" one and a frontend's intent;
 * a custom rompath is what UNKNOWN is for. File I/O: worker threads only.
 */
public class RomLocator {

    public static final int FOUND = 1;
    public static final int MISSING = 0;

    /** Not here, but there are rompaths we cannot see. Do not refuse on it. */
    public static final int UNKNOWN = -1;

    private RomLocator() {
    }

    public static int find(MAME4droid mm, String game) {
        if (mm == null || game == null || game.length() == 0) return UNKNOWN;

        boolean usingSaf = mm.getPrefsHelper().getSAF_Uri() != null;
        ArrayList<String> saf = usingSaf ? mm.getSAFHelper().getRomsFileNames() : null;

        if (matchesAny(saf, game) || inInternalRoms(mm, game) || fromIntent(mm, game))
            return FOUND;

        /* Custom rompath, or a SAF folder we could not read: either way the
         * ROM may well be there and blocking the user would be a guess. */
        if (mm.getPrefsHelper().isUsedMAMEini()) return UNKNOWN;
        if (usingSaf && saf == null) return UNKNOWN;

        return MISSING;
    }

    private static boolean matchesAny(ArrayList<String> names, String game) {
        if (names == null) return false;
        for (String name : names)
            if (matches(name, game)) return true;
        return false;
    }

    /** "./roms" under the installation directory, always on rompath. */
    private static boolean inInternalRoms(MAME4droid mm, String game) {
        String dir = mm.getMainHelper().getInstallationDIR();
        if (dir == null) return false;
        if (!dir.endsWith("/")) dir += "/";

        File roms = new File(dir + "roms");
        if (new File(roms, game).exists()
                || new File(roms, game + ".zip").exists()
                || new File(roms, game + ".7z").exists())
            return true;

        /* Only now list the folder: the filesystem is case sensitive and a
         * ROM copied off a PC can keep its capitals, but walking thousands
         * of entries is not worth it until the three cheap tries have failed. */
        String[] names = roms.list();
        if (names == null) return false;

        for (String name : names)
            if (matches(name, game)) return true;
        return false;
    }

    /**
     * Launched straight into this game by a frontend: the ROM arrived on the
     * intent, so it counts as present wherever it sits. Both shapes work --
     * getFileName() asks the resolver for a content:// display name and falls
     * back to the last path segment for a plain file.
     */
    private static boolean fromIntent(MAME4droid mm, String game) {
        Intent intent = mm.getIntent();
        if (intent == null || !Intent.ACTION_VIEW.equals(intent.getAction())) return false;

        Uri uri = intent.getData();
        if (uri == null) return false;

        String name = mm.getMainHelper().getFileName(uri);
        return name != null && matches(name, game);
    }

    /** The set itself, zipped or as a folder of loose files. */
    private static boolean matches(String name, String game) {
        return name.equalsIgnoreCase(game)
                || name.equalsIgnoreCase(game + ".zip")
                || name.equalsIgnoreCase(game + ".7z");
    }
}
