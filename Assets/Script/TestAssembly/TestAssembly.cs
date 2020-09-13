using System.Collections;
using System.Collections.Generic;
using UnityEngine;

public class TestAssembly
{
	public TestAssembly()
	{
#if UNITY_ANDROID
        assembly_error_android
#elif UNITY_IOS
        assembly_error_ios
#elif !UNITY_EDITOR
        assembly_error_windows_player
#endif
	}
}
