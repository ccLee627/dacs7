﻿using System;
using System.Text.RegularExpressions;

namespace Dacs7.Domain
{

    public enum TagParserState
    {
        Nothing,
        Area,
        Offset,
        Type,
        NumberOfItems,
        TypeValidation,
        Success
    }


    public class TagParser
    {

        public struct TagParserResult
        {
            public PlcArea Area { get; internal set; }
            public ushort DbNumber { get; internal set; }
            public int Offset { get; internal set; }
            public ushort Length { get; internal set; }
            public Type VarType { get; internal set; }
            public Type ResultType { get; internal set; }


            public TagParserState ErrorState { get; internal set; }
        }

        public static TagParserResult ParseTag(string tag)
        {
            return ParseTag(tag, true);
        }


        // DB1.80000,x,1
        public static bool TryParseTag(string tag, out TagParserResult result)
        {
            result = ParseTag(tag, false);
            return result.ErrorState == TagParserState.Success;
        }

        private static TagParserResult ParseTag(string tag, bool throwException)
        {
            var input = tag.ToLower().AsSpan();
            var indexStart = 0;
            var state = TagParserState.Area;
            ReadOnlySpan<char> type = null;
            var result = new TagParserResult();
            for (int i = 0; i < input.Length; i++)
            {
                if (input[i] != '.' && input[i] != ',') continue;
                Parse(tag, ref result, ref indexStart, ref state, ref type, input.Slice(indexStart, i - indexStart), i, true);
            }
            Parse(tag, ref result, ref indexStart, ref state, ref type, input.Slice(indexStart), input.Length - 1, true);


            state = TagParserState.TypeValidation;
            Parse(tag, ref result, ref indexStart, ref state, ref type, input, input.Length - 1, true);

            if(state == TagParserState.Success)
            {
                result.ErrorState = TagParserState.Success;
            }
            return result;
        }

        private static void Parse(string tag, ref TagParserResult result, ref int indexStart, ref TagParserState state, ref ReadOnlySpan<char> type, ReadOnlySpan<char> data, int index, bool throwException = false)
        {
            if (!TryExtractData(ref result, data, ref indexStart, ref state, ref type, index) && throwException)
            {
                result.ErrorState = state;
                throw new Dacs7TagParserException(TagParserState.Area, data.ToString(), tag);
            }
        }

        public static bool TryDetectArea(ReadOnlySpan<char> area, out PlcArea selector, out ushort db)
        {
            db = 0;
            var singleElement = area.Length == 1;
            switch (area[0])
            {
                // Inputs
                case 'i' when singleElement: selector = PlcArea.IB; return true;  // English
                case 'e' when singleElement: selector = PlcArea.IB; return true;  // German
                case 'I' when singleElement: selector = PlcArea.IB; return true;  // English
                case 'E' when singleElement: selector = PlcArea.IB; return true;  // German

                // Marker
                case 'm' when singleElement: selector = PlcArea.FB; return true;  // English and German
                case 'M' when singleElement: selector = PlcArea.FB; return true;  // English and German

                // Ouputs
                case 'q' when singleElement: selector = PlcArea.QB; return true;  // English
                case 'a' when singleElement: selector = PlcArea.QB; return true;  // German

                case 'Q' when singleElement: selector = PlcArea.QB; return true;  // English
                case 'A' when singleElement: selector = PlcArea.QB; return true;  // German

                // Timer
                case 't' when singleElement: selector = PlcArea.TM; return true;  // English and German
                case 'T' when singleElement: selector = PlcArea.TM; return true;  // English and German

                // Counter
                case 'c' when singleElement: selector = PlcArea.CT; return true;  // English
                case 'z' when singleElement: selector = PlcArea.CT; return true;  // German

                case 'C' when singleElement: selector = PlcArea.CT; return true;  // English
                case 'Z' when singleElement: selector = PlcArea.CT; return true;  // German

                case 'd' when area.Length > 2:
                case 'D' when area.Length > 2:
                    {
                        // TODO: ReadOnlySpan<char>   !!!! 
                        // Datablocks
                        if (Regex.IsMatch(area.ToString(), "^db\\d+$", RegexOptions.IgnoreCase))
                        {
                            selector = PlcArea.DB;
#if NETCOREAPP21
                            db = ushort.Parse(area.Slice(2));
#else
                            db = ushort.Parse(area.Slice(2).ToString());
#endif
                            return true;
                        }

                    }
                    break;

            }
            selector = PlcArea.DB;
            return false;
        }



        private static bool TryExtractData(ref TagParserResult result, ReadOnlySpan<char> input, ref int indexStart, ref TagParserState state, ref ReadOnlySpan<char> type, int i)
        {
            switch (state)
            {
                case TagParserState.Area:
                    {
                        if (TryDetectArea(input, out var selector, out var db))
                        {
                            result.Area = selector;
                            result.DbNumber = db;
                            indexStart = i + 1;
                            state = TagParserState.Offset;
                            return true;
                        }
                    }
                    break;
                case TagParserState.Offset:
                    {
                        // TODO:  !!!!!!!
#if NETCOREAPP21
                        if (Int32.TryParse(input, out var offset))
#else
                        if (Int32.TryParse(input.ToString(), out var offset))
#endif

                        {
                            result.Offset = offset;
                            indexStart = i + 1;
                            state = TagParserState.Type;
                            return true;
                        }
                    }
                    break;
                case TagParserState.Type:
                    {
                        type = input;
                        state = TagParserState.NumberOfItems;
                        indexStart = i + 1;
                        return true;
                    }
                case TagParserState.NumberOfItems:
                    {
                        if (input.IsEmpty) return true;
                        // TODO:  !!!!!!!
#if NETCOREAPP21
                        if (UInt16.TryParse(input, out var length))
#else
                        if (UInt16.TryParse(input.ToString(), out var length))
#endif
                        {
                            result.Length = length;
                            state = TagParserState.TypeValidation;
                            return true;
                        }

                    }
                    break;
                case TagParserState.TypeValidation:
                    {
                        if (result.Length <= 0) result.Length = 1;
                        var offset = result.Offset;
                        var length = result.Length;

                        if (!type.IsEmpty && TryDetectTypes(type, ref length, ref offset, out Type vtype, out Type rType))
                        {
                            result.Length = length;
                            result.Offset = offset;
                            result.VarType = vtype;
                            result.ResultType = rType;
                            indexStart = i + 1;
                            state = TagParserState.Success;
                            return true;
                        }
                    }
                    break;
                case TagParserState.Success:
                    return true;
            }
            return false;
        }

        private static bool TryDetectTypes(ReadOnlySpan<char> type, ref ushort length, ref int offset, out Type vtype, out Type rType)
        {
            vtype = typeof(object);
            rType = typeof(object);

            switch (type.ToString())
            {
                case "b":
                    vtype = typeof(byte);
                    rType = length > 1 ? typeof(byte[]) : vtype;
                    return true;
                case "c":
                    vtype = typeof(char);
                    rType = length > 1 ? typeof(char[]) : vtype;
                    return true;
                case "w":
                    vtype = typeof(UInt16);
                    rType = length > 1 ? typeof(UInt16[]) : vtype;
                    return true;
                case "dw":
                    vtype = typeof(UInt32);
                    rType = length > 1 ? typeof(UInt32[]) : vtype;
                    return true;
                case "i":
                    vtype = typeof(Int16);
                    rType = length > 1 ? typeof(Int16[]) : vtype;
                    return true;
                case "di":
                    vtype = typeof(Int32);
                    rType = length > 1 ? typeof(Int32[]) : vtype;
                    return true;
                case "r":
                    vtype = typeof(Single);
                    rType = length > 1 ? typeof(Single[]) : vtype;
                    return true;
                case "s":
                    vtype = rType = typeof(string);
                    //length += 2;
                    //rType = length > 1 ? typeof(string[]) : vtype;
                    return true;
                case var s when Regex.IsMatch(s, "^x\\d+$", RegexOptions.IgnoreCase):
                    vtype = rType = typeof(bool);
                    rType = length > 1 ? typeof(bool[]) : vtype;
#if NETCOREAPP21
                    offset = ((offset * 8) + Int32.Parse(type.Slice(1)));
#else
                     offset = ((offset * 8) + Int32.Parse(type.Slice(1).ToString()));
#endif


                    return true;
            }

            return false;
        }



        }
    }