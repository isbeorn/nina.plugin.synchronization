# Changelog

## Version 0.2.3.0

- Refactored the code for more general puprose sync instructions
- Added a "Synchronized Wait" instruction that can be used to sync up all instances to wait until each one hits the instruction. This can be used for example when a new target is started.
- Make sure that all sequences are running before hitting the synchronization instructions, as only on sequence startup each instance registers itself to the synchronization service

## Version 0.2.2.0

- Add a setting to adjust the maximum dither wait timeout. Previously was fixed to 300 seconds.

## Version 0.2.0.0

- It is now possible to connect only one instance to the guider instead of having all instances to be connected to it. This will make it possible to synchronize other guider sources like the MGEN.
- Heartbeats to the server are only sent when the sequence is running, instead of starting heartbeats on application startup

## Version 0.1.0.0

- Initial release for testing