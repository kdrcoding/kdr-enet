# KDR ENET

Free Windows app. One laptop stays with the car. The other laptop runs E-Sys. They do not need the same Wi-Fi.

<p align="center">
  <a href="https://github.com/kdrcoding/kdr-enet/releases/latest/download/KdrEnet.exe">
    <img src="docs/download-exe.png" width="520" alt="Download KdrEnet.exe">
  </a>
</p>

<p align="center">
  <b>Click the blue button. It downloads KdrEnet.exe.</b><br>
  Windows 10 or 11, 64-bit. When Windows asks, click <b>Yes</b>. You do not install .NET.
</p>

## What to click

1. **How the two laptops connect.** Use **KDR server** when the number at the top is low. Use **Radmin VPN** when it says the server is far, or when you already share a Radmin VPN network. **Open Radmin VPN** starts that program. Both laptops join the same network.
2. **Which laptop is this.** **Car laptop** is the one with the cable. **E-Sys laptop** is the other one.
3. The sentence under those buttons tells you the next click.

On **KDR server**, the car laptop shows 6 numbers. The E-Sys laptop types them and clicks **Join**. In E-Sys use `tcp://127.0.0.1:6801`.

On **Radmin VPN**, the car laptop clicks **Start** and shows an address like `tcp://26.x.x.x:6801`. Paste that address into E-Sys.

**Stop** on both laptops when the coding is done.

<p align="center">
  <img src="docs/car.png" width="360" alt="KDR ENET on the car laptop.">
  &nbsp;&nbsp;
  <img src="docs/esys.png" width="360" alt="KDR ENET on the E-Sys laptop.">
</p>

## Settings

<p align="center">
  <img src="docs/settings.png" width="320" alt="Settings for the cable and car or bike.">
</p>

Pick the cable and **Car** or **Bike**. The code session is for **ENET**, **MHD**, and **ICOM**. K+DCAN only shows that the USB cable is in.

## Cable driver

Windows 10 and 11 usually already have the cable driver. If the cable is plugged in and the app says the driver is missing, click **Open driver page**.

The bottom line names what is missing. **Awake** means the module answered. It is not a battery voltage.

## Before you start

The first time, read About and click **I agree**. You use KDR ENET at your own risk, and only on a vehicle you are allowed to work on. It does not include E-Sys or Radmin VPN. It is not made by BMW.

<p align="center">
  <img src="docs/about.png" width="320" alt="About KDR ENET. You use it at your own risk, and only on a vehicle you are allowed to work on.">
</p>

Created by Kadir · [kdrcoding.com](https://kdrcoding.com) · support@kdrcoding.com
