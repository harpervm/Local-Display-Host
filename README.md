# Local Display Host

Use **any device with a browser** (like your 5K iMac) as a **secondary display** for your Windows PC. The Windows app hosts a local web server; the secondary device opens the URL in a browser and sees a live stream of your Windows screen.

## How it works

1. **Windows PC**: Run the Local Display Host app. Click **Start server**. The app shows the URL (e.g. `http://192.168.1.85:8080/display`).
2. **Secondary device** (e.g. your iMac 5K): On the same network, open that URL in a browser (Safari, Chrome, etc.). The browser shows a live MJPEG stream of the chosen Windows display.
3. **Windows app**: The "Connected displays" list shows when a client (e.g. your iMac) is viewing the stream.

So your Windows PC "sees" the browser client as a secondary display, and you use that device’s browser to view and control the streamed display.

## Requirements

- **Windows PC**: .NET 10, same LAN as the secondary device.
- **Secondary device** (e.g. iMac 5K, tablet, laptop): Any modern browser, same LAN.
- **Network**: Both devices on the same local network (e.g. same Wi‑Fi); firewall may need to allow port 8080.

## Build and run

```bash
cd Local-Display-Host 
dotnet build LocalDisplayHost\LocalDisplayHost.csproj
dotnet run --project LocalDisplayHost\LocalDisplayHost.csproj
```

Or open `LocalDisplay.sln` in Visual Studio and run the **LocalDisplayHost** project.

### Run as .exe

After building, the executable is at:

- **Debug:** `LocalDisplayHost\bin\Debug\net10.0-windows\LocalDisplayHost.exe`

Double‑click the .exe or run it from a terminal. The target PC must have the [.NET 10 runtime for Windows](https://dotnet.microsoft.com/download/dotnet/10.0) installed.

### Publish a standalone .exe (no .NET install needed)

To create a self-contained folder with `LocalDisplayHost.exe` that runs on any Windows x64 PC without installing .NET:

```bash
dotnet publish LocalDisplayHost\LocalDisplayHost.csproj -p:PublishProfile=win-x64
```

Output goes to `publish\win-x64\`. Copy that folder to another PC and run `LocalDisplayHost.exe`; no .NET installation is required.

### Installable setup (.exe) — updatable

To build an installable **Setup.exe** that installs to Program Files, adds Start Menu and optional Desktop shortcut, and appears in Add/Remove Programs:

1. **Install [Inno Setup 6](https://jrsoftware.org/isinfo.php)** (free).
2. Publish the app (if you haven’t already):
   ```bash
   dotnet publish LocalDisplayHost\LocalDisplayHost.csproj -p:PublishProfile=win-x64
   ```
3. Open `installer\LocalDisplayHost.iss` in Inno Setup and click **Build → Compile**, or from a command prompt (e.g. from the repo root):
   ```bash
   "C:\Program Files (x86)\Inno Setup 6\ISCC.exe" installer\LocalDisplayHost.iss
   ```

The setup is written to `publish\LocalDisplayHostSetup-1.0.0.exe` (or the version in the script). Distribute that file; users run it to install.

**To release an update:**  
Edit `installer\LocalDisplayHost.iss`, change the line `#define MyAppVersion "1.0.0"` to a higher version (e.g. `"1.0.1"`), then publish the app and recompile the script. Users can run the new setup to upgrade (the installer will replace the previous version).

## Usage

This application needs a **virtual display** in order to work as an extended display. Install the driver first, then use the app.

### Install Virtual Display Driver

1. Download and install **[Virtual Display Driver](https://github.com/VirtualDrivers/Virtual-Display-Driver/)**.
2. Start the Virtual Display Driver application.
3. In the app, click **Install** to install the driver.
4. Go to Windows Display settings (**Win + I** → System → Display).
5. **Detect** other display (if necessary).
6. Set the new display to **Extend** and change the resolution if needed.

### Stream to your secondary device

1. Start the app on the Windows PC.
2. Under **Display to stream**, choose which display to stream: **Primary**, **Monitor 2**, or **All** (all current screens).
3. Click **Start server**.
4. Copy the displayed URL (e.g. `http://192.168.1.85:8080/display`).
5. On your secondary device (e.g. your iMac 5K), open that URL in a browser. You see the chosen display stream.
6. **Click on the stream** to focus it, then move the mouse and type — input is sent to Windows so you can use that device as an extended display.
7. The Windows app lists connected clients under "Connected displays".

To stop streaming, click **Stop server** on the Windows PC.

## Technical details

- **Server**: TCP HTTP server on port 8080 (no HTTP.SYS), so any device on the LAN can connect via the PC’s IP.
- **Stream**: MJPEG over HTTP (`multipart/x-mixed-replace`). The `/display` page shows an `<img src="/stream">` and sends mouse/key events to `POST /input`.
- **Capture**: Selected display is captured with `Graphics.CopyFromScreen` and encoded as JPEG at ~30 FPS.
- **Input**: Mouse move/click and key events from the browser are injected on Windows via `SendInput` so the client can control the streamed display.

## Firewall

If the secondary device (e.g. your iMac) cannot connect, on the Windows PC allow inbound TCP port **8080** for the app or for all private networks (Windows Defender Firewall or your security software).

## License

Use and modify as you like.
