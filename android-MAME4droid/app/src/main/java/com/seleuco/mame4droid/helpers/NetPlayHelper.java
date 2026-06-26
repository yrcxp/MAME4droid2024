/*
 * This file is part of MAME4droid.
 *
 * Copyright (C) 2025 David Valdeita (Seleuco)
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

import java.net.InetAddress;
import java.net.NetworkInterface;
import java.util.Collections;
import java.util.Enumeration;
import java.util.List;
import java.util.Locale;
import java.util.regex.Pattern;

import android.app.AlertDialog;
import android.app.Dialog;
import android.app.ProgressDialog;
import android.app.Service;
import android.content.DialogInterface;
import android.content.SharedPreferences;
import android.content.SharedPreferences.Editor;
import android.view.View;
import android.view.WindowManager;
import android.view.inputmethod.InputMethodManager;
import android.widget.Button;
import android.widget.EditText;
import android.widget.Toast;
import android.graphics.Color;

import com.seleuco.mame4droid.MAME4droid;
import com.seleuco.mame4droid.Emulator;
import com.seleuco.mame4droid.helpers.PrefsHelper;
import com.seleuco.mame4droid.widgets.WarnWidget;
import com.seleuco.mame4droid.R;


public class NetPlayHelper {

    protected Dialog netplayDlg = null;

    protected ProgressDialog progressDialog = null;

    private boolean canceled = false;

    protected MAME4droid mm = null;

    private static final Pattern IPV4_PATTERN =
            Pattern.compile(
                    "^(25[0-5]|2[0-4]\\d|[0-1]?\\d?\\d)(\\.(25[0-5]|2[0-4]\\d|[0-1]?\\d?\\d)){3}$");

    public NetPlayHelper(MAME4droid mm) {
        this.mm = mm;
    }

    DialogInterface.OnCancelListener dialogCancelListener = new DialogInterface.OnCancelListener() {
        public void onCancel(DialogInterface dialog) {
            Emulator.resume();
        }
    };

    protected void prepareButtons() {

        final Button startButton = (Button) netplayDlg.findViewById(R.id.StartGameBtn);
        final Button joinButton = (Button) netplayDlg.findViewById(R.id.JoinPeerGameBtn);
        final Button disconnectButton = (Button) netplayDlg.findViewById(R.id.DisconnectBtn);

        if (Emulator.getValue(Emulator.NETPLAY_HAS_CONNECTION) == 1) {
            startButton.setEnabled(false);
            joinButton.setEnabled(false);
            disconnectButton.setEnabled(true);
        } else {
            startButton.setEnabled(true);
            joinButton.setEnabled(true);
            disconnectButton.setEnabled(false);
        }

        String name = Emulator.getValueStr(Emulator.GAME_SELECTED);
        if (name != null && name.length() != 0) {
            startButton.setText("Start game: " + name);
        } else {
            startButton.setText("Start game");
            startButton.setEnabled(false);
        }
    }

    public void createDialog() {

        if (!Emulator.isEmulating())
            return;

        netplayDlg = new Dialog(mm);

        netplayDlg.setContentView(R.layout.netplayview);
        netplayDlg.setTitle("Peer-To-Peer NetPlayHelper");
        netplayDlg.setCancelable(true);
        netplayDlg.setOnCancelListener(dialogCancelListener);

        final Button startButton = (Button) netplayDlg.findViewById(R.id.StartGameBtn);
        startButton.setOnClickListener(createGameClick);

        final Button joinButton = (Button) netplayDlg.findViewById(R.id.JoinPeerGameBtn);
        joinButton.setOnClickListener(joinGameClick);

        final Button disconnectButton = (Button) netplayDlg.findViewById(R.id.DisconnectBtn);
        disconnectButton.setOnClickListener(disconnectGameClick);

        prepareButtons();

        netplayDlg.show();
    }

    protected static boolean isIPv4Address(final String input) {
        return IPV4_PATTERN.matcher(input).matches();
    }

    public String getIPAddress() {
        StringBuilder ips = new StringBuilder();
        try {
            List<NetworkInterface> interfaces = Collections.list(NetworkInterface.getNetworkInterfaces());
            
            for (NetworkInterface intf : interfaces) {
                String name = intf.getName().toLowerCase();
                if (name.contains("rmnet") || name.contains("ccmni") || name.contains("p2p") || name.contains("dummy")) continue;
                
                List<InetAddress> addrs = Collections.list(intf.getInetAddresses());
                for (InetAddress addr : addrs) {
                    if (!addr.isLoopbackAddress()) {
                        String sAddr = addr.getHostAddress().toUpperCase(Locale.getDefault());
                        if (isIPv4Address(sAddr)) {
                            if (ips.length() > 0) ips.append("\n");
                            ips.append(sAddr).append(" (").append(name).append(")");
                        }
                    }
                }
            }
        } catch (Exception ex) {
            ex.printStackTrace();
        }
        return ips.length() > 0 ? ips.toString() : null;
    }

    Button.OnClickListener createGameClick = new Button.OnClickListener() {
        public void onClick(View v) {
            createGame();
        }
    };

    Button.OnClickListener joinGameClick = new Button.OnClickListener() {
        public void onClick(View v) {
            AlertDialog.Builder alert = new AlertDialog.Builder(mm);

            alert.setTitle("Enter peer IP Address:");
            //alert.setMessage("Enter peer IP address:");

            final EditText input = new EditText(mm);
            alert.setView(input);

            String ip = mm.getPrefsHelper().getSharedPreferences().getString(PrefsHelper.PREF_NETPLAY_PEERADDR, "");

            input.setText(ip);
            input.setSelection(input.getText().length());

            alert.setPositiveButton("Ok", new DialogInterface.OnClickListener() {
                public void onClick(DialogInterface dialog, int whichButton) {
                    String ip = input.getText().toString();

                    if (ip == null || ip.length() == 0) {
                        new WarnWidget.WarnWidgetHelper(mm, "Connection canceled: Invalid IP address.", 3, Color.RED, false);
                        return;
                    }

                    InputMethodManager imm = (InputMethodManager) mm.getSystemService(Service.INPUT_METHOD_SERVICE);
                    imm.hideSoftInputFromWindow(input.getWindowToken(), 0);

                    SharedPreferences sp = mm.getPrefsHelper().getSharedPreferences();
                    Editor edit = sp.edit();
                    edit.putString(PrefsHelper.PREF_NETPLAY_PEERADDR, ip);
                    edit.commit();

                    joinGame(ip);
                }
            });

            alert.setNegativeButton("Cancel", new DialogInterface.OnClickListener() {
                public void onClick(DialogInterface dialog, int whichButton) {
                    // Canceled.
                }
            });

            AlertDialog dlg = alert.create();
            dlg.getWindow().setSoftInputMode(WindowManager.LayoutParams.SOFT_INPUT_STATE_HIDDEN);
            dlg.show();
        }
    };

    Button.OnClickListener disconnectGameClick = new Button.OnClickListener() {
        public void onClick(View v) {
            Emulator.setValue(Emulator.NETPLAY_HAS_CONNECTION, 0);
            new WarnWidget.WarnWidgetHelper(mm, "NetPlayHelper: Disconnected from the game.", 3, Color.YELLOW, false);
            prepareButtons();
        }
    };

    public void createGame() {

        String strPort = mm.getPrefsHelper().getNetplayPort();
        int port = 0;
        try {
            port = Integer.parseInt(strPort);
        } catch (Exception e) {
        }
        if (!(port >= 1024 && port <= 32768 * 2)) {
            new WarnWidget.WarnWidgetHelper(mm, "Connection canceled: Invalid port.", 3, Color.RED, false);
            return;
        }

        if (Emulator.netplayInit(null, port, 0) == -1) {
            new WarnWidget.WarnWidgetHelper(mm, "NetPlayHelper: Critical error initializing network.", 3, Color.RED, false);
            return;
        }

        //netplayDlg.hide();

        canceled = false;
        progressDialog = ProgressDialog.show(mm, "Press back to cancel",
                "Creating game at ...", true, true,
                new DialogInterface.OnCancelListener() {
                    @Override
                    public void onCancel(DialogInterface dialog) {
                        canceled = true;
                    }
                });

        Thread t = new Thread(new Runnable() {
            public void run() {
                final String ip = getIPAddress();
                if (ip == null) {
                    try {
                        Thread.sleep(2000);
                    } catch (InterruptedException e) {
                        e.printStackTrace();
                    }
                    canceled = true;
                    mm.runOnUiThread(new Runnable() {
                        public void run() {
                            new WarnWidget.WarnWidgetHelper(mm, "Connection canceled: No network available. Check Wi-Fi.", 4, Color.RED, false);
                        }
                    });
                }
                mm.runOnUiThread(new Runnable() {
                    public void run() {
                        progressDialog.setMessage("Waiting for peer...\nCreating game at :" + ip);
                    }
                });
                while (Emulator.getValue(Emulator.NETPLAY_HAS_JOINED) == 0 && !canceled) {
                    try {
                        Thread.sleep(1000);
                        //System.out.println("Esperando...");
                    } catch (InterruptedException e) {
                        e.printStackTrace();
                    }
                }

                if (progressDialog != null && progressDialog.isShowing()) {
                    progressDialog.dismiss();
                }

                if (canceled) {
                    Emulator.setValue(Emulator.NETPLAY_HAS_CONNECTION, 0);
                }

                mm.runOnUiThread(new Runnable() {
                    public void run() {
                        if (!canceled) {
                            if (netplayDlg.isShowing())
                                netplayDlg.hide();
                            new WarnWidget.WarnWidgetHelper(mm, "NetPlayHelper: Connected successfully! Starting game...", 3, Color.GREEN, false);
                            Emulator.resume();
                        }
                    }
                });
            }
        });
        t.start();
    }

    public void joinGame(String addr) {

       String strPort = mm.getPrefsHelper().getNetplayPort();
        int port = 0;
        try {
            port = Integer.parseInt(strPort);
        } catch (Exception e) {
        }
        if (!(port >= 1024 && port <= 32768 * 2)) {
            new WarnWidget.WarnWidgetHelper(mm, "Connection canceled: Invalid port.", 3, Color.RED, false);
            return;
        }

        if (Emulator.netplayInit(addr, port, 0) == -1) {
            new WarnWidget.WarnWidgetHelper(mm, "NetPlayHelper: Critical error initializing network.", 3, Color.RED, false);
            return;
        }

        //netplayDlg.hide();

        canceled = false;
        progressDialog = ProgressDialog.show(mm, "Press back to cancel",
                "Connecting to :" + addr, true, true,
                new DialogInterface.OnCancelListener() {
                    @Override
                    public void onCancel(DialogInterface dialog) {
                        canceled = true;
                    }
                });

        Thread t = new Thread(new Runnable() {
            public void run() {
                while (Emulator.getValue(Emulator.NETPLAY_HAS_JOINED) == 0
                        && !canceled) {
                    try {
                        if (Emulator.netplayInit(null, 0, 1) == -1)
                            canceled = true;
                        Thread.sleep(1000);
                        //System.out.println("Esperando...");
                    } catch (InterruptedException e) {
                        e.printStackTrace();
                    }
                }

                if (progressDialog != null && progressDialog.isShowing()) {
                    progressDialog.dismiss();
                }

                if (canceled) {
                    Emulator.setValue(Emulator.NETPLAY_HAS_CONNECTION, 0);
                }

                mm.runOnUiThread(new Runnable() {
                    public void run() {
                        if (!canceled) {
                            if (netplayDlg.isShowing())
                                netplayDlg.hide();
                            new WarnWidget.WarnWidgetHelper(mm, "NetPlayHelper: Connected successfully! Starting game...", 3, Color.GREEN, false);
                            Emulator.resume();
                        }
                    }
                });
            }
        });
        t.start();
    }

}
