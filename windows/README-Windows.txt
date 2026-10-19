Light Touch for Windows
=======================

Classic Apple devices on Windows: a port of Light Touch (https://github.com/samhenrigold/LightTouchMac),
from the LightTouchWin fork. Each device runs Apple's own firmware in the qemu-ios emulator.

Requirements: Windows 10 or 11, 64-bit (x64). Nothing to install: unzip and run LightTouch.exe.
Keep the files of this folder together; LightTouch.exe loads the emulator (qemu-arm.dll) beside it.

Getting a device
----------------
The Windows build doesn't prepare devices from Apple's IPSWs yet. Two ways in:

1. The built-in iPod touch (2nd generation, iOS 3.1.3), from Light Touch for Mac's download:
   - Download LightTouchMac-universal.zip from https://github.com/samhenrigold/LightTouchMac/releases
     (you don't need a Mac; Windows doesn't need to unzip it).
   - In Light Touch, choose Add > Import from Light Touch for Mac... and pick that zip.
   This also installs the iPod SecureROMs the emulator boots.

2. A device prepared by firmwarekit on a Mac (firmwarekit create ... --out FOLDER): copy FOLDER to the PC and choose
   Add > Import Prepared Device... (the folder holds device.lock.json). iPod touch 1G/2G and iPhone 2G devices need
   the SecureROMs from step 1.

Using a device
--------------
Click and drag on the screen to touch; Alt-drag pinches. Typing goes to the device.
Ctrl+H Home, Ctrl+L Sleep/Wake, Ctrl+Up/Down volume, Ctrl+Left/Right rotate, Ctrl+Shift+Z shake,
Ctrl+V pastes the PC's clipboard text into the device, Ctrl+S saves a screenshot to Pictures\Light Touch.
Closing the window stops the device; its data is saved.

Devices, logs and settings live in %LOCALAPPDATA%\LightTouch. Each device's emulator.log and serial.log are under
Devices\<id>\logs (Device > Show Logs).

Not in this build yet: preparing devices from IPSWs, installing apps (.ipa), media import, the web proxy for
the old devices' HTTPS, and the host GL bridge (guests draw with their own software renderer).

License
-------
Free software under the GNU General Public License, version 2 or later (LICENSE.txt). The bundled components'
licenses and sources are listed under licenses\.

iPhone, iPod touch and iPad are trademarks of Apple Inc. Light Touch is not affiliated with Apple.
