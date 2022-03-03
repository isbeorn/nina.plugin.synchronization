# Changelog

## Version 0.2.2.0

- Add a setting to adjust the maximum dither wait timeout. Previously was fixed to 300 seconds.

## Version 0.2.0.0

- It is now possible to connect only one instance to the guider instead of having all instances to be connected to it. This will make it possible to synchronize other guider sources like the MGEN.
- Heartbeats to the server are only sent when the sequence is running and the sync dither context is active, instead of starting heartbeats on application startup

## Version 0.1.0.0

- Initial release for testing