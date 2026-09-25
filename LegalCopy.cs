namespace KdrEnet;

public static class LegalCopy
{
    public const string TermsVersion = "1.2";
    public const string Creator = "Kadir";
    public const string Brand = "KDR Coding";
    public const string Site = "https://kdrcoding.com";
    public const string Email = "support@kdrcoding.com";

    public const string Terms = """
KDR ENET TERMS OF USE
Version 1.2

These terms are a contract between you and Kadir, operating as KDR Coding ("KDR", "we"), for the KDR ENET software. By clicking I agree, or by starting a session, you accept them. If you do not accept them, do not start a session.

1. What the software does
KDR ENET is a remote-session helper for a car connected to one computer with an ENET cable. The computer with the car shows a session code. The other computer types that code. Both computers connect outward to the KDR session server, and the server passes vehicle diagnostic traffic between them for that code only. E-Sys on the other computer uses 127.0.0.1. Stop, or closing the window, ends the connection. This version does not turn Windows Firewall off. If an older session left the firewall off, Stop restores the settings from before that session.

2. Who may use it
You confirm that you are allowed to change the firewall on this computer, and that you own the vehicle or have the owner's permission to connect to it and to code or diagnose it. You will stay with the computer while the session is on, and you will end the session when the work is finished. You will not use the software on a computer or a vehicle you are not allowed to service.

3. Risk you accept
Vehicle coding and diagnostics can change how a car behaves. A wrong step, a dead battery, a pulled cable, or an open session can affect modules, settings, or this computer. Diagnostic traffic for the session passes through the KDR session server while the session is on. If an older session left the firewall off, this computer accepts inbound network traffic until you click Stop. You understand those risks and you choose to take them. KDR does not promise that a session will succeed, that a feature will work, or that the car or the computer will be unchanged.

4. No warranty
The software is provided as is and as available. To the fullest extent allowed by law, KDR gives no warranty of any kind, whether express, implied, or statutory, including any warranty of merchantability, fitness for a particular purpose, title, quiet enjoyment, accuracy, or non-infringement. We do not warrant that the software will be uninterrupted, error-free, or safe for every car or every network.

5. Limit of liability
To the fullest extent allowed by law, Kadir and KDR Coding will not be liable for any indirect, incidental, special, consequential, exemplary, or punitive damages, or for any loss of data, loss of use, lost profits, vehicle damage, module failure, failed coding, computer damage, privacy or security incident while diagnostic traffic passes through the session server or while the firewall is off from an older session, or cost of substitute service, whether the claim is in contract, tort, or any other theory, and whether or not we were told it might happen. If a law does not allow that full limit, our total liability for all claims arising out of one session is limited to the amount you paid KDR Coding for that session, or fifty United States dollars if you paid nothing for it.

6. Claims by other people
If someone else brings a claim because of your use of the software, the vehicle, or this computer, including a claim about firewall changes, remote access, or coding, you will defend Kadir and KDR Coding and cover the resulting damages, settlements, and reasonable legal costs, to the extent the claim is not caused by our intentional misconduct.

7. Names and independence
KDR ENET is created by Kadir for KDR Coding. It is an independent tool. It is not made by, endorsed by, or affiliated with Bayerische Motoren Werke AG, BMW, ispiexpert, or EsysX. BMW, ENET, E-Sys, and other product names are the property of their owners and are used here only to describe compatibility.

8. If part of this is not enforceable
If a court holds that one part of these terms cannot be enforced, the rest stays in effect. These terms are governed by the laws of the United States and the state where KDR Coding is based, without regard to conflict-of-law rules. Some places do not allow certain warranty disclaimers or liability limits. In those places, the limits apply only as far as the law allows. Nothing in these terms limits a right that the law says cannot be limited.

9. Contact
KDR Coding
Created by Kadir
https://kdrcoding.com
support@kdrcoding.com

Copyright (c) KDR Coding. All rights reserved.
""";
}
