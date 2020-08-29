using System;
using System.Collections.Generic;
using System.Threading.Tasks;

#nullable enable

namespace DebuggerTests
{
    public class NullableTests
    {
        public static void TestNullableLocal()
        {
            int? n_int = 5;
            int? n_int_null = null;

            DateTime? n_dt = new DateTime(2310, 1, 2, 3, 4, 5);
            DateTime? n_dt_null = null;

            ValueTypesTest.GenericStruct<int>? n_gs = new ValueTypesTest.GenericStruct<int> { StringField = "n_gs#StringField" };
            ValueTypesTest.GenericStruct<int>? n_gs_null = null;
            // IL stores the nullable type, and not just null for the _null cases. So, don't remove these!!
            object o_n_i = n_int;
            object o_n_i_null = n_int_null;
            object o_n_gs = n_gs;
            object o_n_gs_null = n_gs_null;
            object o_n_dt = n_dt;
            object o_n_dt_null = n_dt_null;
            object o_n_ia = new int[] {123, 671};

            Console.WriteLine ($"break here");
        }

        public static async Task TestNullableLocalAsync()
        {
            int? n_int = 5;
            int? n_int_null = null;

            DateTime? n_dt = new DateTime(2310, 1, 2, 3, 4, 5);
            DateTime? n_dt_null = null;

            ValueTypesTest.GenericStruct<int>? n_gs = new ValueTypesTest.GenericStruct<int> { StringField = "n_gs#StringField" };
            ValueTypesTest.GenericStruct<int>? n_gs_null = null;

            object o_n_i = n_int;
            object o_n_i_null = n_int_null;
            object o_n_gs = n_gs;
            object o_n_gs_null = n_gs_null;
            object o_n_dt = n_dt;
            object o_n_dt_null = n_dt_null;
            object o_n_ia = new int[] {123, 671};

            Console.WriteLine ($"break here");
            await Task.CompletedTask;
        }
    }
}
