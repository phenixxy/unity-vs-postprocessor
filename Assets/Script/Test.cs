using System.Collections;
using System.Collections.Generic;
using UnityEngine;

public class Test
{
	public Test()
	{
#if UNITY_ANDROID
        error_android
#elif UNITY_IOS
		error_ios
#elif !UNITY_EDITOR
		error_windows_player
#endif

#if CUSTOM_DEFINE
		error_custom_define
#endif
	}
}
