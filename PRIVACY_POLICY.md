## MAME4droid (Current): Privacy policy

Welcome to the MAME4droid (Current)  Emulator app for Android!

This is an open source Android app developed by Seleuco (D.Valdeita). The source code is available on GitHub; the app is also available on Google Play.

As an avid Android user myself, I take privacy very seriously.
I know how irritating it is when apps collect your data without your knowledge.

I hereby state, to the best of my knowledge and belief, that <b>I have not programmed this app to collect any personally identifiable information</b>. All data created by the you (the user) is stored on your device only, and can be simply erased by clearing the app's data or uninstalling it.

There is one part of the app that has to send something over the internet in order to work at all: **Public Rooms**, the optional board used to find someone to play NetPlay with. It is **switched off until you say otherwise**: the first time you open it the app asks, and if you decline it is never contacted. Everything it does is described below.

### Explanation of permissions requested in the app

The list of permissions required by the app can be found in the `AndroidManifest.xml` file:

<br/>

| Permission | Why it is required |
| :---: | --- |
| `android.permission.VIBRATE` | Required to vibrate the device when touch control is used. Permission automatically granted by the system; can't be revoked by user. |
| `android.permission.INTERNET` | Required to download media resources when media scraping is enabled, and to play NetPlay games with another device. Permission automatically granted by the system; can't be revoked by user. |
| `android.permission.ACCESS_WIFI_STATE` | Required to hold a Wi-Fi lock during a NetPlay game, so the radio is not put to sleep mid-match. Permission automatically granted by the system; can't be revoked by user. |
| `android.permission.WAKE_LOCK` | Required by the same Wi-Fi lock above. Permission automatically granted by the system; can't be revoked by user. |
| `android.permission.ACCESS_LOCAL_NETWORK` | Required on Android 17 and later to reach another device on your own Wi-Fi for a local NetPlay game. Asked for only when you start or join one, and the app works without it &mdash; only local games stop working. |

### NetPlay and Public Rooms

A NetPlay game runs **directly between the two devices**. Only controller inputs travel, and nothing about the match passes through any server of mine.

To find each other, the two devices have to learn each other's address. You can do that by hand &mdash; share it over any messenger, exactly as before &mdash; or let the **Public Rooms** board do it for you.

**Public Rooms is optional and you are asked before it is ever used.** You can decline, and you can switch it off at any time under *Options &rarr; Settings &rarr; NetPlay &rarr; Public rooms*. With it off, the app never contacts the board and NetPlay by hand keeps working exactly as it always has.

When it is on, this is what is sent, and only while you are actually using it:

* Your **public IP address and the port** NetPlay uses. There is no way around this: it is what the other player's device dials in order to reach you.
* The **game** you are playing, so people can see what is on offer.
* The **country** your SIM card or your language setting says you are in, shown as a flag on the board.
* When a game ends, **how it went and how fast the connection was**, so I can tell whether the service is working.

And this is what happens to it afterwards:

* **Your address is never written to a log.** It is held in memory only while your room is on the board, handed only to the player who joins you, and gone with the room. The session records the server keeps contain no address at all.
* **The board never shows an address to anyone.** When a room is marked as being on your own network, that is a comparison of two salted fingerprints, not a disclosure of either side.
* **Nothing identifies your device.** No account, no login, no name, no advertising identifier, and nothing stored on your phone that another session could recognise. Two visits from you are not linked to each other.
* **Nothing survives a restart.** Rooms live in memory for about a minute at a time, and the salt used for those fingerprints is generated afresh every time the server starts, so old records cannot be matched against new ones even from the inside.
* There is **no chat and no user-to-user messaging** of any kind.

An IP address can be considered personal data in some jurisdictions, which is why it is spelled out here rather than buried: it is used to introduce two players and for nothing else, it is never stored, and you decide whether any of it happens.

If you run your own board (the server is open source too), the app talks to that one instead, and none of the above involves me at all.

 <hr style="border:1px solid gray">

If you find any security vulnerability that has been inadvertently caused by me, or have any question regarding how the app protectes your privacy, please send me an email or post a discussion on GitHub, and I will surely try to fix it/help you.

Yours sincerely,  
Seleuco.
<br/>
seleuco.nicator@gmail.com
