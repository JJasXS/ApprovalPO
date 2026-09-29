# Chainway C5 companion app

This folder is a Xamarin.Android wrapper around the manufacturer's UHF demo. It shows the ApprovalPO website in a WebView and sends tag EPCs into the page. It is not the manufacturer's `UHF.apk`.

The ASP.NET site does not reference `DeviceAPI_Android.dll`. The reader runs only on the handheld.

## What was copied from the demo

Unpacked from `Demo-UHF_xamarin.rar` (`demo_uhf_uart/UHF`):

- Trigger keys in `MainActivity.OnKeyDown`: key codes **139**, **280**, and **293**, and only when `RepeatCount == 0`.
- Reader calls in `Scan_Fragment`: `RFIDWithUHFUART.Instance`, `Init()`, `StartInventoryTag()`, `ReadTagFromBuffer()`, `InventorySingleTag()`, `StopInventory()`, and `Free()` from `MainActivity.OnDestroy`.
- `OnPause` stops inventory, matching `Scan_Fragment.OnPause`.
- Project target from `demo_uhf_uart.csproj`: `TargetFrameworkVersion` **v11.0** (API 30). Manifest `minSdkVersion` **23**, `targetSdkVersion` **30**.

The demo does not stop the reader on key up. This app calls the demo's `StopInventory()` when the trigger is released.

`site_origin` in `Resources/values/Strings.xml` must be the same HTTPS address as `Approval:PublicBaseUrl` in the website config (for example `https://your-server:2096/`). Leave it empty and the app will not open a site. The JavaScript bridge runs only when the WebView address host and port match that origin.

Package id: `com.proacc.approvalpo.chainway`. Keep this id for every update.

## This PC cannot build the APK

Checked on the machine that added this project:

- No JDK (`java` is not on PATH).
- No Android SDK under `C:\Program Files\Android`.
- No Visual Studio Xamarin targets (`Xamarin.Android.CSharp.targets` was not found).
- The unpacked demo imports `$(MSBuildExtensionsPath)\Novell\Novell.MonoDroid.CSharp.targets`. This project imports `$(MSBuildExtensionsPath)\Xamarin\Android\Xamarin.Android.CSharp.targets`, which is the Visual Studio 2022 Xamarin.Android path. If a machine only has the older Novell import, point the last line of `ChainwayC5.csproj` at that file.

No APK was produced. Do not copy the demo `UHF.apk` into the website.

## Build on a machine that has the workload

1. Install Visual Studio 2022 with the **Mobile development with .NET** (Xamarin.Android) workload, a JDK, and the Android SDK with platform **android-30**.
2. Open `android/ChainwayC5/ChainwayC5.sln`.
3. Set `Resources/values/Strings.xml` `site_origin` to the live HTTPS origin. Use the same value as `Approval:PublicBaseUrl`.
4. Create a new upload keystore. Do not reuse `android.keystore` from the unpacked demo. Keep the store password and key password out of git (this repo ignores `*.keystore`).

```bat
keytool -genkeypair -v -keystore chainway-upload.keystore -alias chainway -keyalg RSA -keysize 2048 -validity 10000
```

5. In the Release property group of `ChainwayC5.csproj`, set `AndroidKeyStore` to `true` and set `AndroidSigningKeyStore`, `AndroidSigningKeyAlias`, `AndroidSigningStorePass`, and `AndroidSigningKeyPass` on that build machine only. Do not commit the passwords. Use the same keystore and alias for every later version, and increase `android:versionCode` in `Properties/AndroidManifest.xml`.
6. Build **Release**. `EmbedAssembliesIntoApk` is true and `AndroidUseSharedRuntime` is false so the APK can be installed by itself. The demo's Release configuration left the shared Mono runtime on, which is not suitable for a handheld install.
7. Copy the signed APK to `wwwroot/downloads/ChainwayC5-RFID.apk` on the ApprovalPO server. The site already serves `.apk` as `application/vnd.android.package-archive`. A service restart is not required for a new static file.

## Install on the Chainway C5

1. On the handheld, open the ApprovalPO site in the browser over HTTPS and sign in if you want, or use the app login below.
2. On the home page (Scanning PO) or the Scanning PO menu, tap **Download RFID Scanner**.
3. When Android asks, allow the browser to install unknown apps. There is no silent install.
4. Open **Approval PO RFID**. It loads `site_origin`. Sign in with the existing email OTP login.
5. Open a purchase order scan (listening stays on) or Detect RFID and press **Start scan**.
6. Pull the physical trigger. Release it to stop a continuous read.

Desktop browsers and ordinary phones keep working. They never load this app unless someone taps the download link.
