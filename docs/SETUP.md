# ControllerHelper setup guide

**English** | [Русский](SETUP.ru.md) · [Project overview](../README.md)

This guide describes the current v1.1.0 interface. Button names below match the English UI. Configure each computer separately: display identifiers and pairing credentials from another PC are not a ready-to-use profile.

## Contents

- [1. Prepare and launch](#1-prepare-and-launch)
- [2. Save the PC setup](#2-save-the-pc-setup)
- [3. Set up the TV picture and sound](#3-set-up-the-tv-picture-and-sound)
- [4. Configure the controller and recovery shortcut](#4-configure-the-controller-and-recovery-shortcut)
- [5. Test switching and use both screens](#5-test-switching-and-use-both-screens)
- [6. Configure network control](#6-configure-network-control)
- [7. Set up LG webOS](#7-set-up-lg-webos)
- [8. Set up Hisense VIDAA](#8-set-up-hisense-vidaa)
- [9. Use Flydigi](#9-use-flydigi)
- [10. Everyday use, updates, and settings](#10-everyday-use-updates-and-settings)
- [Troubleshooting](#troubleshooting)

## 1. Prepare and launch

You need Windows 11 x64, a working controller available through Microsoft GameInput, and a TV connected to the PC's video output. Install Steam and open it once: **the current “Play on TV” action requires Steam and always opens Big Picture**. There is no setting to disable that step. Flydigi is optional.

1. Download `ControllerHelper-…-win-x64.zip` from the [latest release](https://github.com/eleaf33/controllerhelper/releases/latest).
2. Extract the entire archive into a writable folder, for example `Documents\ControllerHelper`. Do not run it inside the ZIP.
3. Keep `ControllerHelper.exe`, `lib`, `lang`, and `build-manifest.json` together. The top-level EXE is the only file you launch; it still needs the other files.
4. Start that EXE normally, without administrator privileges. No separate .NET installation is required.
5. To use Russian, select **PC → Language / Язык → Русский**, click **Save settings**, choose **Exit** from the tray icon, and start the app again. English is the default.

For initial setup, turn on the TV with its remote and select the HDMI port connected to the PC. In **Windows Settings → System → Display**, use **Identify** to distinguish your screens and confirm that Windows can show a picture on the TV. You can temporarily extend the desktop onto the TV while saving its layout.

Leave network wake off and **Input when switching to TV** set to **Do not switch input** for the first local test. Add network control after display switching works.

**Public-release limitation:** the downloadable build does not contain Hisense private protocol keys. It supports local display/audio switching and Wake-on-LAN, but Hisense MQTT pairing, input selection, and power-off require a local build with the [compatibility materials](../README.md#local-hisense-materials). LG does not have this limitation.

## 2. Save the PC setup

1. In Windows Display settings, arrange your desktop monitors as you want them when returning from the TV. Set the main monitor, resolutions, and refresh rates there.
2. Open ControllerHelper's **PC** tab. Click **Refresh** if devices have changed.
3. In **Monitors**, select all desktop displays that should return together. **Do not select the TV.** Every selected monitor must currently be active when you save its layout.
4. In **PC audio output**, select the speakers, headphones, or audio interface you normally use at the desk. This output also provides the fallback when HDMI audio is unavailable.
5. Click **Save PC layout**, then **Save settings**. Check the status message at the bottom for success.

**Save PC layout** captures the selected monitors' current Windows configuration. **Save settings** saves the selections and options; it does not replace capturing a display layout. If you change the selected monitor set, save its layout again while those monitors are active.

## 3. Set up the TV picture and sound

### Picture

1. Open **TV** and choose the television in the **TV** list. A suffix such as `1234:1` is a Windows device identifier, not an HDMI input number.
2. With the selected TV active in Windows, click **Save TV layout**. This saves a known working configuration; it does not turn the TV on or switch to it.
3. Click **Refresh TV modes** to read the selected display's available modes. Some values can only be read while the TV is active in Windows.
4. For the first test, leave **Resolution** and **TV-only scaling** at **Do not change**. To request a particular mode later, choose its **Resolution** and **Refresh rate** from the lists, then save.
5. Click **Save settings**.

Choosing “Play on TV” activates only the selected TV. The **Turn on monitors** overlay action adds the saved PC monitors afterward.

“Do not change” leaves the corresponding extra override unset; the saved display profile still takes part in switching. If a stored mode is marked unavailable, refresh while the TV is active and choose a mode currently offered by Windows. Custom system-wide scaling can prevent per-display scaling; you can leave **TV-only scaling** at **Do not change** and configure scaling in Windows instead.

### Audio, including HDMI and soundbars

1. Open **TV audio output**. Its device list refreshes each time you open it.
2. Select the HDMI audio output associated with the TV or its GPU connection. Its name is supplied by Windows/the driver and may include the TV model, NVIDIA, AMD, or Intel.
3. You can save an output marked **not connected** before switching. HDMI audio may become available only when the TV's display path becomes active.
4. If an output is marked **disabled in Windows**, enable that playback device in Windows Sound settings first if you want to use it. ControllerHelper does not enable devices that Windows has disabled.
5. Save the settings. After display activation, ControllerHelper waits up to **10 seconds** for the selected TV output. If it remains unavailable, it tries the selected PC output and reports a warning; an audio failure does not by itself return the picture to the monitors.

For `PC → HDMI → TV → HDMI ARC/eARC → soundbar`, the soundbar may not appear as a separate PC audio device. Choose the HDMI endpoint Windows exposes for that connection and configure the TV's sound output to the soundbar. Check this route in Windows first. If no HDMI endpoint is listed, activate the TV display, reopen the list, and inspect Windows playback devices. A saved selection marked missing is an old endpoint, not proof that the current connection uses it.

You can select the same working audio device for PC and TV while testing display switching.

## 4. Configure the controller and recovery shortcut

1. Connect the controller and open **Controller**.
2. Select the intended device in **Controller** and check **Connection**. If it is absent, confirm that Windows detects it and check any GameInput error shown by the app.
3. Under **Overlay shortcut**, choose two different buttons on that controller. The default is **A + View**, held for about **0.5 seconds**. View is the Xbox-style back/view button, not Menu.
4. To identify an unfamiliar button, click **Capture first button** or **Capture second button**, then press the desired controller button.
5. Enable **Emergency return to monitors** before testing a new display setup if you want a recovery shortcut. The default button choices are **LB + RB**, held for **2 seconds**; the feature itself is off by default. Choose a combination different from the overlay shortcut.
6. Click **Save settings**.

Recovery restores the saved PC layout and PC audio and attempts to close Steam Big Picture. It works while ControllerHelper is responsive and the selected controller is connected. **There is no automatic countdown that reverts the display switch.** Keep a keyboard/mouse available during the first test.

## 5. Test switching and use both screens

With the TV on, the PC's HDMI input selected, both layouts saved, and Steam installed:

1. Click **Play on TV** on the TV tab. The TV becomes the active display, audio changes, and Steam Big Picture opens.
2. Hold the overlay shortcut. Use the **D-pad** to select a card, **A** to confirm, or **B** to dismiss the overlay.
3. Choose **Turn on monitors**. The saved PC monitors become available **alongside the TV**, which stays active. This action does not change the selected audio output or close Big Picture.
4. The second card now reads **Turn off monitors**. It removes the PC monitors from the active display setup while keeping the TV active.
5. Choose **Return to monitors** to restore the PC profile and PC audio and close Big Picture. Network power-off depends on the TV setup and the action shown; it is separate from the monitor toggle.

| Overlay action | What it does |
| --- | --- |
| Play on TV | Uses the TV display/audio profile and opens Big Picture; performs configured wake/input commands. |
| Turn on monitors | Adds the saved monitors to the TV setup; TV, audio, and Big Picture remain in use. |
| Turn off monitors | Removes those monitors while the TV remains active. |
| Return to monitors | Restores the PC profile/audio and closes Big Picture. Paired Hisense also attempts network power-off. |
| Turn off TV and return to monitors | Restores PC displays/audio first, then attempts the configured TV power-off. This label depends on network state. |
| Resume game / Close overlay | Dismisses the overlay without switching display profiles. |
| Flydigi | Opens the separately installed Flydigi client. |
| Close Telegram | Requests closure of Telegram; this is a separate action from display switching. |

The monitor toggle is available while the selected TV is an active display. In the combined setup, the app places the saved monitor group to the right of the active TV area. A full return restores the saved PC layout. A game may react to display changes in its own way.

## 6. Configure network control

**Video connection and network control are separate.** HDMI carries the PC picture/sound; the local network supplies wake, input-selection, and power commands.

| Field / control | What to enter or do |
| --- | --- |
| Network control for the selected TV | Select **LG** or **Hisense**, then expand its matching block. The selector chooses which TV integration is used. |
| Local IPv4 address | The TV's address on your home network, for example `192.168.1.50`; not the router's address or a public internet IP. |
| MAC address | The address of the TV's active Ethernet or Wi-Fi interface. Those interfaces have different MAC addresses. |
| Check connection | Checks local reachability and tries to resolve the MAC address. It is not pairing and does not verify HDMI. |
| Wake … over the network | Sends Wake-on-LAN when you choose Play on TV. The TV must support network wake and remain in standby. |
| Input when switching to TV | The TV input physically connected to this PC, such as HDMI 2. This is independent of Windows display numbering. |

To fill the MAC automatically, turn the TV on, enter its IP, leave the MAC field empty, and click **Check connection**. The app fills it only if local address resolution succeeds; it does not replace an existing MAC. If it cannot resolve the address, obtain it from the TV's network information or your router. Entering an IP alone is not enough for Wake-on-LAN: the app needs a valid MAC before saving enabled wake.

The PC and TV must be on the same local IPv4 subnet for this implementation. Guest Wi-Fi/client isolation can prevent communication. If the router changes the TV's IP, update it in the app; a DHCP reservation can keep it stable. A successful connection check does not guarantee wake from standby. Sending a wake packet also does not prove the TV woke up, and Wake-on-LAN alone does not choose HDMI.

## 7. Set up LG webOS

1. Turn on LG manually and enable its supported mobile/network control options. Option names vary by webOS version; **TV On With Mobile** alone does not guarantee standard Wake-on-LAN support.
2. Select **LG** in the network provider list, expand **LG**, enter its local IP, and run **Check connection**. Resolve or enter the correct MAC before enabling **Wake LG over the network**.
3. Leave **Allow legacy unencrypted mode** unchecked for normal secure setup. Click **Check TV certificate**, verify that the shown address is your TV, and confirm the certificate if you trust that device. Certificate dates are checked.
4. Click **Pair TV** and accept the permission prompt on the television using its remote. Wait for the app's pairing status.
5. With the TV still on, click **Refresh TV inputs** inside the LG block. The general **Refresh** button at the bottom updates Windows devices; it is not the TV's input-list button.
6. Select the PC's physical HDMI port in **Input when switching to TV**, then click **Save settings**. An input marked **no signal** can still be selected.
7. Test with LG showing Apple TV or another source: choose **Play on TV** and check that LG changes to the PC input before the Windows display switch.
8. Test waking from standby separately after switching with an already-on TV works.

To leave input selection to the remote, select **Do not switch input**. Input selection can be used with a TV that is already on; it does not require enabling network wake. If LG rejects input switching, pair it again and accept the new permission request. After changing TV/IP/certificate details, recheck trust and pairing. Legacy unencrypted mode is for older compatible TVs; it sends the control key in plain text and should be a deliberate choice on a trusted local network.

## 8. Set up Hisense VIDAA

Support depends on the model and firmware; the integration was developed against **Hisense 55U7QF / VIDAA**. Hisense models with other operating systems are not covered by that compatibility claim.

### Wake-on-LAN in the public build

1. Enable the TV's supported **Wake on LAN** option. Wireless wake additionally requires the TV's **Wake on Wireless Network** support. Menu wording varies.
2. Select **Hisense**, expand its block, enter the local IP, and click **Check connection** while the TV is on. Fill the MAC of the actual Ethernet or Wi-Fi connection.
3. Enable **Wake Hisense over the network**, leave **Input when switching to TV** at **Do not switch input**, and click **Save settings**.
4. Put the TV in standby with its remote. Click **Test wake without switching displays** and check whether the TV turns on. This test sends wake packets without changing Windows screens.
5. Without MQTT input control, select the PC's HDMI manually. If your VIDAA version offers a startup **Last Source** option, you can use it and select the PC's HDMI before standby. It is a TV setting, not a command sent by Wake-on-LAN.

### PIN pairing, HDMI selection, and power-off

These steps require a local build containing the [Hisense compatibility materials](../README.md#local-hisense-materials). They cannot be enabled in the public ZIP simply by entering a PIN.

1. Turn on the TV manually and enable its supported mobile/remote-control feature. No phone application is required to pair ControllerHelper.
2. Enter and save the correct TV/network details. Click **Pair Hisense**.
3. Review and confirm the TV certificate prompt, then enter the **four-digit PIN displayed on the television** in ControllerHelper.
4. After pairing succeeds, click **Refresh TV inputs** if needed, choose the PC's input in **Input when switching to TV**, and save.
5. Test **Play on TV**. When configured, it sends Wake-on-LAN and attempts input selection before changing Windows displays.
6. Test the full **Return to monitors** action. Once paired, Hisense power-off is attempted after PC displays/audio are restored. **Turn on monitors** keeps the TV active instead.

If pairing fails, check the app's exact error, TV remote-control permissions, PC/TV date and time, and logs. Do not replace protocol keys at random. Compatibility with a working phone remote does not by itself prove that this build supports the same firmware/protocol profile.

## 9. Use Flydigi

Install Flydigi Space Station separately and confirm that its normal shortcut opens a usable interface. ControllerHelper looks for its EXE in standard installation locations; there is no custom path picker in the current UI.

Start both programs normally. Select **Flydigi** in the overlay and allow the client time to display its own window. The left stick moves the pointer, A clicks or confirms keyboard navigation, and B sends Escape to the client. Holding X for about one second closes the Flydigi window through its own close behavior. Cursor speed can be adjusted in the overlay.

If Windows reports access denied, fully exit Flydigi through its tray icon and start it normally again. If the client is blank only inside the overlay, compare with its standalone shortcut and collect logs; a visible window handle alone does not establish successful rendering.

## 10. Everyday use, updates, and settings

- Closing the settings window leaves ControllerHelper in the system tray. Use its tray menu to reopen settings or exit the app completely.
- Keep it running for controller shortcuts. The current UI does not include an automatic-startup switch; you can create a shortcut to the top-level EXE in your Windows Startup folder if desired.
- Before updating, exit the old instance through the tray. Extract the new release in full and launch its top-level EXE. Starting another copy while one is running forwards the request to the existing process.
- Program files are portable; settings are per Windows user, in `%LOCALAPPDATA%\ControllerHelper\settings.json`, with a `.bak` backup. On the same PC/user, another extracted build uses those settings.
- Pairing tokens are protected for the current Windows user. On a different PC, select the displays/audio again, capture layouts, choose the controller, and pair the TV again.
- Logs are written automatically to `logs` beside the top-level EXE, independently of the working directory. Use a folder where your account can write.

## Troubleshooting

| Symptom | What to check |
| --- | --- |
| TV is unavailable or absent from the list | Turn it on, choose the PC's HDMI input, confirm it works in Windows Display settings, then Refresh and select it again. A network connection alone does not make an HDMI display available. |
| Save PC/TV layout fails | The screens being captured must be active in Windows. The TV must not also be selected in the PC monitor list. |
| The app asks for Steam | Install and start Steam so its `steam://` handler is registered. Steam is currently required by Play on TV. |
| The TV is still showing Apple TV | Use **Refresh TV inputs** in the correct brand block, select the PC port, save, and confirm pairing permissions. Windows display numbers are not HDMI input numbers. |
| HDMI sound is missing | Activate the TV display, reopen the audio list, and check Windows playback devices. Verify the chosen endpoint rather than an old missing device. The TV may expose audio for a connected soundbar. |
| Sound stays on PC speakers | The selected TV output may not have become available within 10 seconds. Check the warning and logs; PC audio is the intended fallback. |
| A resolution/refresh rate is unavailable | Activate the TV in Windows, Refresh TV modes, and choose an advertised mode. Use Do not change for the first test. |
| MAC is not filled automatically | Check connection while the TV is on and the field is empty. If resolution fails, enter the active interface's MAC manually. |
| Wake packets are sent but nothing happens | Check standby network-wake support, the active interface's MAC, subnet, and network isolation. Sending packets is not confirmation of wake. |
| Hisense reports missing compatibility materials | The public ZIP excludes private keys. Use local switching/Wake-on-LAN or build locally with the required materials; PIN entry alone cannot fix it. |
| The overlay shortcut does nothing | Check the selected controller and Connection status, save the two distinct buttons, and hold them for about 0.5 seconds. The app must be running. |
| Settings appear unchanged after an update | Exit the existing tray instance before launching the new EXE. Language changes also require saving and restarting. |

For a reproducible problem, note the exact action and time, application version, Windows version, GPU/driver, TV model, connection route, and whether Windows can switch the picture/audio manually. Attach `logs/launcher.log` and `logs/errors.jsonl`; include `.1` files if the attempt was rotated out. `errors.jsonl` also contains normal diagnostic events. Review logs before publishing; do not share `settings.json`, pairing credentials, or private keys.
