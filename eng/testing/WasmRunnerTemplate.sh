#!/usr/bin/env bash

EXECUTION_DIR=$(dirname $0)

cd $EXECUTION_DIR

XHARNESS_OUT="$EXECUTION_DIR/xharness-output"

if [ ! -z "$XHARNESS_CLI_PATH" ]; then
	# When running in CI, we only have the .NET runtime available
	# We need to call the XHarness CLI DLL directly via dotnet exec
	HARNESS_RUNNER="dotnet exec $XHARNESS_CLI_PATH"
else
	HARNESS_RUNNER="dotnet xharness"
fi

if [ -z "$XHARNESS_COMMAND" ]; then
	XHARNESS_COMMAND="test"
fi

DEBUG_PROXY_PROJ=$HOME/dev/runtime/src/mono/wasm/debugger/BrowserDebugHost/BrowserDebugHost.csproj
DEBUG_PROXY_PORT=9300
BROWSER_DEBUG_PORT=9222
XHARNESS_ARGS="$XHARNESS_ARGS --debug-port=$BROWSER_DEBUG_PORT"

dotnet run -p $DEBUG_PROXY_PROJ $BROWSER_DEBUG_PORT $DEBUG_PROXY_PORT &
echo $?

# RunCommands defined in tests.mobile.targets
[[RunCommands]]

_exitCode=$?

echo "XHarness artifacts: $XHARNESS_OUT"

exit $_exitCode
