KarmaKontroller driver note
===========================

The WorldCup USB driver in this folder may be unsigned on modern Windows
systems. Windows may refuse to load it unless driver signature enforcement is
disabled or Windows is placed into test-signing mode.

Use these steps only if Windows refuses to install or load the driver.

Enable test-signing mode
------------------------

1. Open Command Prompt or PowerShell as Administrator.
2. Run:

   bcdedit /set testsigning on

3. Restart the PC.
4. Install the driver from this folder:

   WorldCup_Device.inf

5. When test mode is enabled, Windows normally shows "Test Mode" on the
   desktop.

Return Windows to normal mode
-----------------------------

1. Open Command Prompt or PowerShell as Administrator.
2. Run:

   bcdedit /set testsigning off

3. Restart the PC.

Temporary alternative
---------------------

Windows also has a one-boot "Disable driver signature enforcement" option in
Advanced startup. That avoids leaving test-signing mode enabled, but it must be
selected again if the driver needs to be installed after another reboot.

Safety notes
------------

Only install drivers from a source you trust. Re-enable normal driver signature
enforcement when you are finished if you do not need test-signing mode.
