// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System;
using System.Collections.Generic;
using System.Text;
using System.Diagnostics;
using System.Xml;

namespace System.Runtime.Serialization.Json
{
    internal sealed class JsonQNameDataContract : JsonDataContract
    {
        public JsonQNameDataContract(QNameDataContract traditionalQNameDataContract)
            : base(traditionalQNameDataContract)
        {
        }

        public override object? ReadJsonValueCore(XmlReaderDelegator jsonReader, XmlObjectSerializerReadContextComplexJson? context)
        {
            if (context == null)
            {
                return TryReadNullAtTopLevel(jsonReader) ? null : jsonReader.ReadElementContentAsQName();
            }
            else
            {
                return HandleReadValue(jsonReader.ReadElementContentAsQName(), context);
            }
        }
    }
}
