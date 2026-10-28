# GlassBogo development

- Keep this an entry manager, not a file manager. Entry/category operations must never move, overwrite or delete user targets.
- The implementation and visual assets are independent. Do not copy GlassDesk source, shaders or branding into this repository.
- User data belongs outside the checkout. Never commit real entry targets, calendar tasks, logs, databases or private screenshots.
- Keep disk/COM work off the UI thread. Bound icon/page caches and stop subscriptions/timers with their owner.
- Run core and Windows service tests for relevant changes. Real WPF window tests must use DesktopHarness on an isolated desktop; never switch the user's desktop or run window-spawning tests on their active work surface.
- Performance targets are measured, not inferred from successful compilation. Record the build, data scale and limitations of each result.
- Install/update the same per-user location and shortcut; preserve user data and recovery records.
- Maintain third-party notices and do not assign a license to the user's original work without their instruction.
