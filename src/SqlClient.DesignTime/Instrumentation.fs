module internal Instrumentation

module Log =
  open Serilog
  open Serilog.Configuration

  Log.Logger <- (new LoggerConfiguration()).WriteTo.Console().WriteTo.Debug().CreateLogger()


  open System.Diagnostics
  let scopedMessage message warnIfLongerThanMs f =
    #if INSTRUMENTED
    let stopwatch = Stopwatch()
    stopwatch.Start()
    Log.Logger.Information(sprintf "= [%s] starting =============================" message)
    #endif
    let result = f ()
    #if INSTRUMENTED
    stopwatch.Stop()
    let elapsed = stopwatch.ElapsedMilliseconds
    if elapsed > warnIfLongerThanMs then
      Log.Logger.Warning(sprintf "= completed in %ims =============================" elapsed)
    else
      Log.Logger.Information(sprintf "= completed in %ims =============================" elapsed)
    #endif
    result