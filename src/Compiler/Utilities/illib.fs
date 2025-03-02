// Copyright (c) Microsoft Corporation. All Rights Reserved. See License.txt in the project root for license information.

namespace Internal.Utilities.Library

open System
open System.Collections.Generic
open System.Collections.Concurrent
open System.Diagnostics
open System.IO
open System.Threading
open System.Threading.Tasks
open System.Runtime.CompilerServices
#if !FSHARPCORE_USE_PACKAGE
open FSharp.Core.CompilerServices.StateMachineHelpers
#endif

[<AutoOpen>]
module internal PervasiveAutoOpens =
    /// Logical shift right treating int32 as unsigned integer.
    /// Code that uses this should probably be adjusted to use unsigned integer types.
    let (>>>&) (x: int32) (n: int32) = int32 (uint32 x >>> n)

    let notlazy v = Lazy<_>.CreateFromValue v

    let inline isNil l = List.isEmpty l

    /// Returns true if the list has less than 2 elements. Otherwise false.
    let inline isNilOrSingleton l =
        match l with
        | []
        | [ _ ] -> true
        | _ -> false

    /// Returns true if the list contains exactly 1 element. Otherwise false.
    let inline isSingleton l =
        match l with
        | [ _ ] -> true
        | _ -> false

    type 'T MaybeNull when 'T: null and 'T: not struct = 'T

    let inline isNotNull (x: 'T) = not (isNull x)

    let inline (|NonNullQuick|) (x: 'T MaybeNull) =
        match x with
        | null -> raise (NullReferenceException())
        | v -> v

    let inline nonNull (x: 'T MaybeNull) =
        match x with
        | null -> raise (NullReferenceException())
        | v -> v

    let inline (|Null|NonNull|) (x: 'T MaybeNull) : Choice<unit, 'T> =
        match x with
        | null -> Null
        | v -> NonNull v

    let inline nullArgCheck paramName (x: 'T MaybeNull) =
        match x with
        | null -> raise (ArgumentNullException(paramName))
        | v -> v

    let inline (===) x y = LanguagePrimitives.PhysicalEquality x y

    /// Per the docs the threshold for the Large Object Heap is 85000 bytes: https://learn.microsoft.com/dotnet/standard/garbage-collection/large-object-heap#how-an-object-ends-up-on-the-large-object-heap-and-how-gc-handles-them
    /// We set the limit to be 80k to account for larger pointer sizes for when F# is running 64-bit.
    let LOH_SIZE_THRESHOLD_BYTES = 80_000

    type String with

        member inline x.StartsWithOrdinal value =
            x.StartsWith(value, StringComparison.Ordinal)

        member inline x.EndsWithOrdinal value =
            x.EndsWith(value, StringComparison.Ordinal)

        member inline x.EndsWithOrdinalIgnoreCase value =
            x.EndsWith(value, StringComparison.OrdinalIgnoreCase)

    /// Get an initialization hole
    let getHole (r: _ ref) =
        match r.Value with
        | None -> failwith "getHole"
        | Some x -> x

    let reportTime =
        let mutable tPrev: IDisposable = null

        fun descr ->
            if isNotNull tPrev then
                tPrev.Dispose()

            tPrev <-
                if descr <> "Finish" then
                    FSharp.Compiler.Diagnostics.Activity.Profiling.startAndMeasureEnvironmentStats descr
                else
                    null

    let foldOn p f z x = f z (p x)

    let notFound () = raise (KeyNotFoundException())

    type Async with

        static member RunImmediate(computation: Async<'T>, ?cancellationToken) =
            let cancellationToken = defaultArg cancellationToken Async.DefaultCancellationToken
            let ts = TaskCompletionSource<'T>()
            let task = ts.Task

            Async.StartWithContinuations(
                computation,
                (fun k -> ts.SetResult k),
                (fun exn -> ts.SetException exn),
                (fun _ -> ts.SetCanceled()),
                cancellationToken
            )

            task.Result

/// An efficient lazy for inline storage in a class type. Results in fewer thunks.
[<Struct>]
type InlineDelayInit<'T when 'T: not struct> =
    new(f: unit -> 'T) =
        {
            store = Unchecked.defaultof<'T>
            func = Func<_>(f)
        }

    val mutable store: 'T
    val mutable func: Func<'T> MaybeNull

    member x.Value =
        match x.func with
        | null -> x.store
        | _ ->
            let res = LazyInitializer.EnsureInitialized(&x.store, x.func)
            x.func <- null
            res

//-------------------------------------------------------------------------
// Library: projections
//------------------------------------------------------------------------

module Order =
    let orderBy (p: 'T -> 'U) =
        { new IComparer<'T> with
            member _.Compare(x, xx) = compare (p x) (p xx)
        }

    let orderOn p (pxOrder: IComparer<'U>) =
        { new IComparer<'T> with
            member _.Compare(x, xx) = pxOrder.Compare(p x, p xx)
        }

    let toFunction (pxOrder: IComparer<'U>) x y = pxOrder.Compare(x, y)

//-------------------------------------------------------------------------
// Library: arrays, lists, options, resizearrays
//-------------------------------------------------------------------------

module Array =

    let mapq f inp =
        match inp with
        | [||] -> inp
        | _ ->
            let res = Array.map f inp
            let len = inp.Length
            let mutable eq = true
            let mutable i = 0

            while eq && i < len do
                if not (inp[i] === res[i]) then
                    eq <- false

                i <- i + 1

            if eq then inp else res

    let lengthsEqAndForall2 p l1 l2 =
        Array.length l1 = Array.length l2 && Array.forall2 p l1 l2

    let order (eltOrder: IComparer<'T>) =
        { new IComparer<'T array> with
            member _.Compare(xs, ys) =
                let c = compare xs.Length ys.Length

                if c <> 0 then
                    c
                else
                    let rec loop i =
                        if i >= xs.Length then
                            0
                        else
                            let c = eltOrder.Compare(xs[i], ys[i])
                            if c <> 0 then c else loop (i + 1)

                    loop 0
        }

    let existsOne p l =
        let rec forallFrom p l n =
            (n >= Array.length l) || (p l[n] && forallFrom p l (n + 1))

        let rec loop p l n =
            (n < Array.length l)
            && (if p l[n] then
                    forallFrom (fun x -> not (p x)) l (n + 1)
                else
                    loop p l (n + 1))

        loop p l 0

    let existsTrue (arr: bool[]) =
        let rec loop n =
            (n < arr.Length) && (arr[n] || loop (n + 1))

        loop 0

    let findFirstIndexWhereTrue (arr: _[]) p =
        let rec look lo hi =
            assert ((lo >= 0) && (hi >= 0))
            assert ((lo <= arr.Length) && (hi <= arr.Length))

            if lo = hi then
                lo
            else
                let i = (lo + hi) / 2

                if p arr[i] then
                    if i = 0 then i
                    else if p arr[i - 1] then look lo i
                    else i
                else
                    // not true here, look after
                    look (i + 1) hi

        look 0 arr.Length

    /// pass an array byref to reverse it in place
    let revInPlace (array: 'T[]) =
        if Array.isEmpty array then
            ()
        else
            let arrLen, revLen = array.Length - 1, array.Length / 2 - 1

            for idx in 0..revLen do
                let t1 = array[idx]
                let t2 = array[arrLen - idx]
                array[idx] <- t2
                array[arrLen - idx] <- t1

    /// Async implementation of Array.map.
    let mapAsync (mapping: 'T -> Async<'U>) (array: 'T[]) : Async<'U[]> =
        let len = Array.length array
        let result = Array.zeroCreate len

        async { // Apply the mapping function to each array element.
            for i in 0 .. len - 1 do
                let! mappedValue = mapping array[i]
                result[i] <- mappedValue

            // Return the completed results.
            return result
        }

    /// Returns a new array with an element replaced with a given value.
    let replace index value (array: _[]) =
        if index >= array.Length then
            raise (IndexOutOfRangeException "index")

        let res = Array.copy array
        res[index] <- value
        res

    /// Optimized arrays equality. ~100x faster than `array1 = array2` on strings.
    /// ~2x faster for floats
    /// ~0.8x slower for ints
    let inline areEqual (xs: 'T[]) (ys: 'T[]) =
        match xs, ys with
        | null, null -> true
        | [||], [||] -> true
        | null, _
        | _, null -> false
        | _ when xs.Length <> ys.Length -> false
        | _ ->
            let mutable break' = false
            let mutable i = 0
            let mutable result = true

            while i < xs.Length && not break' do
                if xs[i] <> ys[i] then
                    break' <- true
                    result <- false

                i <- i + 1

            result

    /// Returns all heads of a given array.
    /// For [|1;2;3|] it returns [|[|1; 2; 3|]; [|1; 2|]; [|1|]|]
    let heads (array: 'T[]) =
        let res = Array.zeroCreate<'T[]> array.Length

        for i = array.Length - 1 downto 0 do
            res[i] <- array[0..i]

        res

    /// check if subArray is found in the wholeArray starting
    /// at the provided index
    let inline isSubArray (subArray: 'T[]) (wholeArray: 'T[]) index =
        if subArray.Length = 0 then
            true
        elif subArray.Length > wholeArray.Length then
            false
        elif subArray.Length = wholeArray.Length then
            areEqual subArray wholeArray
        else
            let rec loop subidx idx =
                if subidx = subArray.Length then
                    true
                elif subArray[subidx] = wholeArray[idx] then
                    loop (subidx + 1) (idx + 1)
                else
                    false

            loop 0 index

    /// Returns true if one array has another as its subset from index 0.
    let startsWith (prefix: _[]) (whole: _[]) = isSubArray prefix whole 0

    /// Returns true if one array has trailing elements equal to another's.
    let endsWith (suffix: _[]) (whole: _[]) =
        isSubArray suffix whole (whole.Length - suffix.Length)

module Option =

    let mapFold f s opt =
        match opt with
        | None -> None, s
        | Some x ->
            let x2, s2 = f s x
            Some x2, s2

    let attempt (f: unit -> 'T) =
        try
            Some(f ())
        with _ ->
            None

module List =

    let sortWithOrder (c: IComparer<'T>) elements =
        List.sortWith (Order.toFunction c) elements

    let splitAfter n l =
        let rec split_after_acc n l1 l2 =
            if n <= 0 then
                List.rev l1, l2
            else
                split_after_acc (n - 1) ((List.head l2) :: l1) (List.tail l2)

        split_after_acc n [] l

    let existsi f xs =
        let rec loop i xs =
            match xs with
            | [] -> false
            | h :: t -> f i h || loop (i + 1) t

        loop 0 xs

    let lengthsEqAndForall2 p l1 l2 =
        List.length l1 = List.length l2 && List.forall2 p l1 l2

    let rec findi n f l =
        match l with
        | [] -> None
        | h :: t -> if f h then Some(h, n) else findi (n + 1) f t

    let splitChoose select l =
        let rec ch acc1 acc2 l =
            match l with
            | [] -> List.rev acc1, List.rev acc2
            | x :: xs ->
                match select x with
                | Choice1Of2 sx -> ch (sx :: acc1) acc2 xs
                | Choice2Of2 sx -> ch acc1 (sx :: acc2) xs

        ch [] [] l

    let rec checkq l1 l2 =
        match l1, l2 with
        | h1 :: t1, h2 :: t2 -> h1 === h2 && checkq t1 t2
        | _ -> true

    let mapq (f: 'T -> 'T) inp =
        assert not typeof<'T>.IsValueType

        match inp with
        | [] -> inp
        | [ h1a ] ->
            let h2a = f h1a
            if h1a === h2a then inp else [ h2a ]
        | [ h1a; h1b ] ->
            let h2a = f h1a
            let h2b = f h1b

            if h1a === h2a && h1b === h2b then inp else [ h2a; h2b ]
        | [ h1a; h1b; h1c ] ->
            let h2a = f h1a
            let h2b = f h1b
            let h2c = f h1c

            if h1a === h2a && h1b === h2b && h1c === h2c then
                inp
            else
                [ h2a; h2b; h2c ]
        | _ ->
            let res = List.map f inp
            if checkq inp res then inp else res

    let frontAndBack l =
        let rec loop acc l =
            match l with
            | [] ->
                Debug.Assert(false, "empty list")
                invalidArg "l" "empty list"
            | [ h ] -> List.rev acc, h
            | h :: t -> loop (h :: acc) t

        loop [] l

    let tryFrontAndBack l =
        match l with
        | [] -> None
        | _ -> Some(frontAndBack l)

    let tryRemove f inp =
        let rec loop acc l =
            match l with
            | [] -> None
            | h :: t -> if f h then Some(h, List.rev acc @ t) else loop (h :: acc) t

        loop [] inp

    let zip4 l1 l2 l3 l4 =
        List.zip l1 (List.zip3 l2 l3 l4)
        |> List.map (fun (x1, (x2, x3, x4)) -> (x1, x2, x3, x4))

    let unzip4 l =
        let a, b, cd = List.unzip3 (List.map (fun (x, y, z, w) -> (x, y, (z, w))) l)
        let c, d = List.unzip cd
        a, b, c, d

    let rec iter3 f l1 l2 l3 =
        match l1, l2, l3 with
        | h1 :: t1, h2 :: t2, h3 :: t3 ->
            f h1 h2 h3
            iter3 f t1 t2 t3
        | [], [], [] -> ()
        | _ -> failwith "iter3"

    let takeUntil p l =
        let rec loop acc l =
            match l with
            | [] -> List.rev acc, []
            | x :: xs -> if p x then List.rev acc, l else loop (x :: acc) xs

        loop [] l

    let order (eltOrder: IComparer<'T>) =
        { new IComparer<'T list> with
            member _.Compare(xs, ys) =
                let rec loop xs ys =
                    match xs, ys with
                    | [], [] -> 0
                    | [], _ -> -1
                    | _, [] -> 1
                    | x :: xs, y :: ys ->
                        let cxy = eltOrder.Compare(x, y)
                        if cxy = 0 then loop xs ys else cxy

                loop xs ys
        }

    let indexNotFound () =
        raise (KeyNotFoundException("An index satisfying the predicate was not found in the collection"))

    let rec assoc x l =
        match l with
        | [] -> indexNotFound ()
        | (h, r) :: t -> if x = h then r else assoc x t

    let rec memAssoc x l =
        match l with
        | [] -> false
        | (h, _) :: t -> x = h || memAssoc x t

    let rec memq x l =
        match l with
        | [] -> false
        | h :: t -> LanguagePrimitives.PhysicalEquality x h || memq x t

    let mapNth n f xs =
        let rec mn i =
            function
            | [] -> []
            | x :: xs -> if i = n then f x :: xs else x :: mn (i + 1) xs

        mn 0 xs

    let count pred xs =
        List.fold (fun n x -> if pred x then n + 1 else n) 0 xs

    let headAndTail l =
        match l with
        | [] -> failwith "headAndTail"
        | h :: t -> (h, t)

    // WARNING: not tail-recursive
    let mapHeadTail fhead ftail =
        function
        | [] -> []
        | [ x ] -> [ fhead x ]
        | x :: xs -> fhead x :: List.map ftail xs

    let collectFold f s l =
        let l, s = List.mapFold f s l
        List.concat l, s

    let collect2 f xs ys = List.concat (List.map2 f xs ys)

    let toArraySquared xss =
        xss |> List.map List.toArray |> List.toArray

    let iterSquared f xss = xss |> List.iter (List.iter f)

    let collectSquared f xss = xss |> List.collect (List.collect f)

    let mapSquared f xss = xss |> List.map (List.map f)

    let mapFoldSquared f z xss = List.mapFold (List.mapFold f) z xss

    let forallSquared f xss = xss |> List.forall (List.forall f)

    let mapiSquared f xss =
        xss |> List.mapi (fun i xs -> xs |> List.mapi (fun j x -> f i j x))

    let existsSquared f xss =
        xss |> List.exists (fun xs -> xs |> List.exists (fun x -> f x))

    let mapiFoldSquared f z xss =
        mapFoldSquared f z (xss |> mapiSquared (fun i j x -> (i, j, x)))

    let duplicates (xs: 'T list) =
        xs
        |> List.groupBy id
        |> List.filter (fun (_, elems) -> Seq.length elems > 1)
        |> List.map fst

    let internal allEqual (xs: 'T list) =
        match xs with
        | [] -> true
        | h :: t -> t |> List.forall (fun h2 -> h = h2)

    let isSingleton xs =
        match xs with
        | [ _ ] -> true
        | _ -> false

module ResizeArray =

    /// Split a ResizeArray into an array of smaller chunks.
    /// This requires `items/chunkSize` Array copies of length `chunkSize` if `items/chunkSize % 0 = 0`,
    /// otherwise `items/chunkSize + 1` Array copies.
    let chunkBySize chunkSize f (items: ResizeArray<'t>) =
        // we could use Seq.chunkBySize here, but that would involve many enumerator.MoveNext() calls that we can sidestep with a bit of math
        let itemCount = items.Count

        if itemCount = 0 then
            [||]
        else
            let chunksCount =
                match itemCount / chunkSize with
                | n when itemCount % chunkSize = 0 -> n
                | n -> n + 1 // any remainder means we need an additional chunk to store it

            [|
                for index in 0 .. chunksCount - 1 do
                    let startIndex = index * chunkSize
                    let takeCount = min (itemCount - startIndex) chunkSize

                    let holder = Array.zeroCreate takeCount
                    // we take a bounds-check hit here on each access.
                    // other alternatives here include
                    // * iterating across an IEnumerator (incurs MoveNext penalty)
                    // * doing a block copy using `List.CopyTo(index, array, index, count)` (requires more copies to do the mapping)
                    // none are significantly better.
                    for i in 0 .. takeCount - 1 do
                        holder[i] <- f items[startIndex + i]

                    yield holder
            |]

    /// Split a large ResizeArray into a series of array chunks that are each under the Large Object Heap limit.
    /// This is done to help prevent a stop-the-world collection of the single large array, instead allowing for a greater
    /// probability of smaller collections. Stop-the-world is still possible, just less likely.
    let mapToSmallArrayChunks f (inp: ResizeArray<'t>) =
        let itemSizeBytes = sizeof<'t>
        // rounding down here is good because it ensures we don't go over
        let maxArrayItemCount = LOH_SIZE_THRESHOLD_BYTES / itemSizeBytes

        // chunk the provided input into arrays that are smaller than the LOH limit
        // in order to prevent long-term storage of those values
        chunkBySize maxArrayItemCount f inp

module ValueOptionInternal =

    let inline ofOption x =
        match x with
        | Some x -> ValueSome x
        | None -> ValueNone

    let inline bind f x =
        match x with
        | ValueSome x -> f x
        | ValueNone -> ValueNone

module String =
    let make (n: int) (c: char) : string = String(c, n)

    let get (str: string) i = str[i]

    let sub (s: string) (start: int) (len: int) = s.Substring(start, len)

    let contains (s: string) (c: char) = s.IndexOf c <> -1

    let order = LanguagePrimitives.FastGenericComparer<string>

    let lowercase (s: string) = s.ToLowerInvariant()

    let uppercase (s: string) = s.ToUpperInvariant()

    // Scripts that distinguish between upper and lower case (bicameral) DU Discriminators and Active Pattern identifiers are required to start with an upper case character.
    // For valid identifiers where the case of the identifier can not be determined because there is no upper and lower case we will allow DU Discriminators and upper case characters
    // to be used.  This means that developers using unicameral scripts such as hindi, are not required to prefix these identifiers with an Upper case latin character.
    //
    let isLeadingIdentifierCharacterUpperCase (s: string) =
        let isUpperCaseCharacter c =
            // if IsUpper and IsLower return the same value, then we can't tell if it's upper or lower case, so ensure it is a letter
            // otherwise it is bicameral, so must be upper case
            let isUpper = Char.IsUpper c

            if isUpper = Char.IsLower c then
                Char.IsLetter c
            else
                isUpper

        s.Length >= 1 && isUpperCaseCharacter s[0]

    let capitalize (s: string) =
        if s.Length = 0 then
            s
        else
            uppercase s[0..0] + s[1 .. s.Length - 1]

    let uncapitalize (s: string) =
        if s.Length = 0 then
            s
        else
            lowercase s[0..0] + s[1 .. s.Length - 1]

    let dropPrefix (s: string) (t: string) = s[t.Length .. s.Length - 1]

    let dropSuffix (s: string) (t: string) = s[0 .. s.Length - t.Length - 1]

    let inline toCharArray (str: string) = str.ToCharArray()

    let lowerCaseFirstChar (str: string) =
        if String.IsNullOrEmpty str || Char.IsLower(str, 0) then
            str
        else
            let strArr = toCharArray str

            match Array.tryHead strArr with
            | None -> str
            | Some c ->
                strArr[0] <- Char.ToLower c
                String strArr

    let extractTrailingIndex (str: string) =
        let charr = str.ToCharArray()
        Array.revInPlace charr
        let digits = Array.takeWhile Char.IsDigit charr
        Array.revInPlace digits

        String digits
        |> function
            | "" -> str, None
            | index -> str.Substring(0, str.Length - index.Length), Some(int index)

    /// Splits a string into substrings based on the strings in the array separators
    let split options (separator: string[]) (value: string) = value.Split(separator, options)

    let (|StartsWith|_|) pattern value =
        if String.IsNullOrWhiteSpace value then None
        elif value.StartsWithOrdinal pattern then Some()
        else None

    let (|Contains|_|) pattern value =
        if String.IsNullOrWhiteSpace value then None
        elif value.Contains pattern then Some()
        else None

    let getLines (str: string) =
        use reader = new StringReader(str)

        [|
            let mutable line = reader.ReadLine()

            while not (isNull line) do
                yield line
                line <- reader.ReadLine()

            if str.EndsWithOrdinal("\n") then
                // last trailing space not returned
                // http://stackoverflow.com/questions/19365404/stringreader-omits-trailing-linebreak
                yield String.Empty
        |]

module Dictionary =
    let inline newWithSize (size: int) =
        Dictionary<_, _>(size, HashIdentity.Structural)

    let inline ofList (xs: ('Key * 'Value) list) =
        let t = Dictionary<_, _>(List.length xs, HashIdentity.Structural)

        for k, v in xs do
            t.Add(k, v)

        t

[<Extension>]
type DictionaryExtensions() =

    [<Extension>]
    static member inline BagAdd(dic: Dictionary<'key, 'value list>, key: 'key, value: 'value) =
        match dic.TryGetValue key with
        | true, values -> dic[key] <- value :: values
        | _ -> dic[key] <- [ value ]

    [<Extension>]
    static member inline BagExistsValueForKey(dic: Dictionary<'key, 'value list>, key: 'key, f: 'value -> bool) =
        match dic.TryGetValue key with
        | true, values -> values |> List.exists f
        | _ -> false

module Lazy =
    let force (x: Lazy<'T>) = x.Force()

//----------------------------------------------------------------------------
// Single threaded execution and mutual exclusion

/// Represents a permission active at this point in execution
type ExecutionToken =
    interface
    end

/// Represents a token that indicates execution on the compilation thread, i.e.
///   - we have full access to the (partially mutable) TAST and TcImports data structures
///   - compiler execution may result in type provider invocations when resolving types and members
///   - we can access various caches in the SourceCodeServices
///
/// Like other execution tokens this should be passed via argument passing and not captured/stored beyond
/// the lifetime of stack-based calls. This is not checked, it is a discipline within the compiler code.
[<Sealed>]
type CompilationThreadToken() =
    interface ExecutionToken

/// A base type for various types of tokens that must be passed when a lock is taken.
/// Each different static lock should declare a new subtype of this type.
type LockToken =
    inherit ExecutionToken

/// Represents a token that indicates execution on any of several potential user threads calling the F# compiler services.
[<Sealed>]
type AnyCallerThreadToken() =
    interface ExecutionToken

[<AutoOpen>]
module internal LockAutoOpens =
    /// Represents a place where we are stating that execution on the compilation thread is required. The
    /// reason why will be documented in a comment in the code at the callsite.
    let RequireCompilationThread (_ctok: CompilationThreadToken) = ()

    /// Represents a place in the compiler codebase where we are passed a CompilationThreadToken unnecessarily.
    /// This represents code that may potentially not need to be executed on the compilation thread.
    let DoesNotRequireCompilerThreadTokenAndCouldPossiblyBeMadeConcurrent (_ctok: CompilationThreadToken) = ()

    /// Represents a place in the compiler codebase where we assume we are executing on a compilation thread
    let AssumeCompilationThreadWithoutEvidence () =
        Unchecked.defaultof<CompilationThreadToken>

    let AnyCallerThread = Unchecked.defaultof<AnyCallerThreadToken>

    let AssumeLockWithoutEvidence<'LockTokenType when 'LockTokenType :> LockToken> () = Unchecked.defaultof<'LockTokenType>

/// Encapsulates a lock associated with a particular token-type representing the acquisition of that lock.
type Lock<'LockTokenType when 'LockTokenType :> LockToken>() =
    let lockObj = obj ()

    member _.AcquireLock f =
        lock lockObj (fun () -> f (AssumeLockWithoutEvidence<'LockTokenType>()))

//---------------------------------------------------
// Misc

module Map =
    let tryFindMulti k map =
        match Map.tryFind k map with
        | Some res -> res
        | None -> []

[<Struct>]
type ResultOrException<'TResult> =
    | Result of result: 'TResult
    | Exception of ``exception``: Exception

module ResultOrException =

    let success a = Result a

    let raze (b: exn) = Exception b

    // map
    let (|?>) res f =
        match res with
        | Result x -> Result(f x)
        | Exception err -> Exception err

    let ForceRaise res =
        match res with
        | Result x -> x
        | Exception err -> raise err

    let otherwise f x =
        match x with
        | Result x -> success x
        | Exception _err -> f ()

[<RequireQualifiedAccess; Struct>]
type ValueOrCancelled<'TResult> =
    | Value of result: 'TResult
    | Cancelled of ``exception``: OperationCanceledException

/// Represents a cancellable computation with explicit representation of a cancelled result.
///
/// A cancellable computation is passed may be cancelled via a CancellationToken, which is propagated implicitly.
/// If cancellation occurs, it is propagated as data rather than by raising an OperationCanceledException.
[<Struct>]
type Cancellable<'T> = Cancellable of (CancellationToken -> ValueOrCancelled<'T>)

module Cancellable =

    /// Run a cancellable computation using the given cancellation token
    let inline run (ct: CancellationToken) (Cancellable oper) =
        if ct.IsCancellationRequested then
            ValueOrCancelled.Cancelled(OperationCanceledException ct)
        else
            oper ct

    let fold f acc seq =
        Cancellable(fun ct ->
            let mutable acc = ValueOrCancelled.Value acc

            for x in seq do
                match acc with
                | ValueOrCancelled.Value accv -> acc <- run ct (f accv x)
                | ValueOrCancelled.Cancelled _ -> ()

            acc)

    /// Run the computation in a mode where it may not be cancelled. The computation never results in a
    /// ValueOrCancelled.Cancelled.
    let runWithoutCancellation comp =
        let res = run CancellationToken.None comp

        match res with
        | ValueOrCancelled.Cancelled _ -> failwith "unexpected cancellation"
        | ValueOrCancelled.Value r -> r

    let toAsync c =
        async {
            let! ct = Async.CancellationToken
            let res = run ct c

            return!
                Async.FromContinuations(fun (cont, _econt, ccont) ->
                    match res with
                    | ValueOrCancelled.Value v -> cont v
                    | ValueOrCancelled.Cancelled ce -> ccont ce)
        }

    /// Bind the cancellation token associated with the computation
    let token () =
        Cancellable(fun ct -> ValueOrCancelled.Value ct)

type CancellableBuilder() =

    member inline _.Delay([<InlineIfLambda>] f) =
        Cancellable(fun ct ->
            let (Cancellable g) = f ()
            g ct)

    member inline _.Bind(comp, [<InlineIfLambda>] k) =
        Cancellable(fun ct ->
#if !FSHARPCORE_USE_PACKAGE
            __debugPoint ""
#endif

            match Cancellable.run ct comp with
            | ValueOrCancelled.Value v1 -> Cancellable.run ct (k v1)
            | ValueOrCancelled.Cancelled err1 -> ValueOrCancelled.Cancelled err1)

    member inline _.BindReturn(comp, [<InlineIfLambda>] k) =
        Cancellable(fun ct ->
#if !FSHARPCORE_USE_PACKAGE
            __debugPoint ""
#endif

            match Cancellable.run ct comp with
            | ValueOrCancelled.Value v1 -> ValueOrCancelled.Value(k v1)
            | ValueOrCancelled.Cancelled err1 -> ValueOrCancelled.Cancelled err1)

    member inline _.Combine(comp1, comp2) =
        Cancellable(fun ct ->
#if !FSHARPCORE_USE_PACKAGE
            __debugPoint ""
#endif

            match Cancellable.run ct comp1 with
            | ValueOrCancelled.Value () -> Cancellable.run ct comp2
            | ValueOrCancelled.Cancelled err1 -> ValueOrCancelled.Cancelled err1)

    member inline _.TryWith(comp, [<InlineIfLambda>] handler) =
        Cancellable(fun ct ->
#if !FSHARPCORE_USE_PACKAGE
            __debugPoint ""
#endif

            let compRes =
                try
                    match Cancellable.run ct comp with
                    | ValueOrCancelled.Value res -> ValueOrCancelled.Value(Choice1Of2 res)
                    | ValueOrCancelled.Cancelled exn -> ValueOrCancelled.Cancelled exn
                with err ->
                    ValueOrCancelled.Value(Choice2Of2 err)

            match compRes with
            | ValueOrCancelled.Value res ->
                match res with
                | Choice1Of2 r -> ValueOrCancelled.Value r
                | Choice2Of2 err -> Cancellable.run ct (handler err)
            | ValueOrCancelled.Cancelled err1 -> ValueOrCancelled.Cancelled err1)

    member inline _.Using(resource, [<InlineIfLambda>] comp) =
        Cancellable(fun ct ->
#if !FSHARPCORE_USE_PACKAGE
            __debugPoint ""
#endif
            let body = comp resource

            let compRes =
                try
                    match Cancellable.run ct body with
                    | ValueOrCancelled.Value res -> ValueOrCancelled.Value(Choice1Of2 res)
                    | ValueOrCancelled.Cancelled exn -> ValueOrCancelled.Cancelled exn
                with err ->
                    ValueOrCancelled.Value(Choice2Of2 err)

            match compRes with
            | ValueOrCancelled.Value res ->
                Microsoft.FSharp.Core.LanguagePrimitives.IntrinsicFunctions.Dispose resource

                match res with
                | Choice1Of2 r -> ValueOrCancelled.Value r
                | Choice2Of2 err -> raise err
            | ValueOrCancelled.Cancelled err1 -> ValueOrCancelled.Cancelled err1)

    member inline _.TryFinally(comp, [<InlineIfLambda>] compensation) =
        Cancellable(fun ct ->
#if !FSHARPCORE_USE_PACKAGE
            __debugPoint ""
#endif

            let compRes =
                try
                    match Cancellable.run ct comp with
                    | ValueOrCancelled.Value res -> ValueOrCancelled.Value(Choice1Of2 res)
                    | ValueOrCancelled.Cancelled exn -> ValueOrCancelled.Cancelled exn
                with err ->
                    ValueOrCancelled.Value(Choice2Of2 err)

            match compRes with
            | ValueOrCancelled.Value res ->
                compensation ()

                match res with
                | Choice1Of2 r -> ValueOrCancelled.Value r
                | Choice2Of2 err -> raise err
            | ValueOrCancelled.Cancelled err1 -> ValueOrCancelled.Cancelled err1)

    member inline _.Return v =
        Cancellable(fun _ -> ValueOrCancelled.Value v)

    member inline _.ReturnFrom(v: Cancellable<'T>) = v

    member inline _.Zero() =
        Cancellable(fun _ -> ValueOrCancelled.Value())

[<AutoOpen>]
module CancellableAutoOpens =
    let cancellable = CancellableBuilder()

/// Generates unique stamps
type UniqueStampGenerator<'T when 'T: equality>() =
    let encodeTable = ConcurrentDictionary<'T, Lazy<int>>(HashIdentity.Structural)
    let mutable nItems = -1

    let computeFunc = Func<'T, _>(fun _ -> lazy (Interlocked.Increment(&nItems)))

    member _.Encode str =
        encodeTable.GetOrAdd(str, computeFunc).Value

    member _.Table = encodeTable.Keys

/// memoize tables (all entries cached, never collected)
type MemoizationTable<'T, 'U>(compute: 'T -> 'U, keyComparer: IEqualityComparer<'T>, ?canMemoize) =

    let table = new ConcurrentDictionary<'T, Lazy<'U>>(keyComparer)
    let computeFunc = Func<_, _>(fun key -> lazy (compute key))

    member t.Apply x =
        if
            (match canMemoize with
             | None -> true
             | Some f -> f x)
        then
            table.GetOrAdd(x, computeFunc).Value
        else
            compute x

/// A thread-safe lookup table which is assigning an auto-increment stamp with each insert
type internal StampedDictionary<'T, 'U>(keyComparer: IEqualityComparer<'T>) =
    let table = new ConcurrentDictionary<'T, Lazy<int * 'U>>(keyComparer)
    let mutable count = -1

    member _.Add(key, value) =
        let entry = table.GetOrAdd(key, lazy (Interlocked.Increment(&count), value))
        entry.Force() |> ignore

    member _.UpdateIfExists(key, valueReplaceFunc) =
        match table.TryGetValue key with
        | true, v ->
            let (stamp, oldVal) = v.Value

            match valueReplaceFunc oldVal with
            | None -> ()
            | Some newVal -> table.TryUpdate(key, lazy (stamp, newVal), v) |> ignore<bool>
        | _ -> ()

    member _.GetAll() =
        table |> Seq.map (fun kvp -> kvp.Key, kvp.Value.Value)

exception UndefinedException

type LazyWithContextFailure(exn: exn) =

    static let undefined = LazyWithContextFailure(UndefinedException)

    member _.Exception = exn

    static member Undefined = undefined

/// Just like "Lazy" but EVERY forcer must provide an instance of "ctxt", e.g. to help track errors
/// on forcing back to at least one sensible user location
[<DefaultAugmentation(false)>]
[<NoEquality; NoComparison>]
type LazyWithContext<'T, 'Ctxt> =
    {
        /// This field holds the result of a successful computation. It's initial value is Unchecked.defaultof
        mutable value: 'T

        /// This field holds either the function to run or a LazyWithContextFailure object recording the exception raised
        /// from running the function. It is null if the thunk has been evaluated successfully.
        mutable funcOrException: obj

        /// A helper to ensure we rethrow the "original" exception
        findOriginalException: exn -> exn
    }

    static member Create(f: 'Ctxt -> 'T, findOriginalException) : LazyWithContext<'T, 'Ctxt> =
        {
            value = Unchecked.defaultof<'T>
            funcOrException = box f
            findOriginalException = findOriginalException
        }

    static member NotLazy(x: 'T) : LazyWithContext<'T, 'Ctxt> =
        {
            value = x
            funcOrException = null
            findOriginalException = id
        }

    member x.IsDelayed =
        (match x.funcOrException with
         | null -> false
         | :? LazyWithContextFailure -> false
         | _ -> true)

    member x.IsForced =
        (match x.funcOrException with
         | null -> true
         | _ -> false)

    member x.Force(ctxt: 'Ctxt) =
        match x.funcOrException with
        | null -> x.value
        | _ ->
            // Enter the lock in case another thread is in the process of evaluating the result
            Monitor.Enter x

            try
                x.UnsynchronizedForce ctxt
            finally
                Monitor.Exit x

    member x.UnsynchronizedForce ctxt =
        match x.funcOrException with
        | null -> x.value
        | :? LazyWithContextFailure as res ->
            // Re-raise the original exception
            raise (x.findOriginalException res.Exception)
        | :? ('Ctxt -> 'T) as f ->
            x.funcOrException <- box (LazyWithContextFailure.Undefined)

            try
                let res = f ctxt
                x.value <- res
                x.funcOrException <- null
                res
            with exn ->
                x.funcOrException <- box (LazyWithContextFailure(exn))
                reraise ()
        | _ -> failwith "unreachable"

/// Intern tables to save space.
module Tables =
    let memoize f =
        let t =
            ConcurrentDictionary<_, _>(Environment.ProcessorCount, 1000, HashIdentity.Structural)

        fun x ->
            match t.TryGetValue x with
            | true, res -> res
            | _ ->
                let res = f x
                t[x] <- res
                res

/// Interface that defines methods for comparing objects using partial equality relation
type IPartialEqualityComparer<'T> =
    inherit IEqualityComparer<'T>
    /// Can the specified object be tested for equality?
    abstract InEqualityRelation: 'T -> bool

module IPartialEqualityComparer =

    let On f (c: IPartialEqualityComparer<_>) =
        { new IPartialEqualityComparer<_> with
            member _.InEqualityRelation x = c.InEqualityRelation(f x)
            member _.Equals(x, y) = c.Equals(f x, f y)
            member _.GetHashCode x = c.GetHashCode(f x)
        }

    // Wrapper type for use by the 'partialDistinctBy' function
    [<StructuralEquality; NoComparison>]
    type private WrapType<'T> = Wrap of 'T

    // Like Seq.distinctBy but only filters out duplicates for some of the elements
    let partialDistinctBy (per: IPartialEqualityComparer<'T>) seq =
        let wper =
            { new IPartialEqualityComparer<WrapType<'T>> with
                member _.InEqualityRelation(Wrap x) = per.InEqualityRelation x
                member _.Equals(Wrap x, Wrap y) = per.Equals(x, y)
                member _.GetHashCode(Wrap x) = per.GetHashCode x
            }
        // Wrap a Wrap _ around all keys in case the key type is itself a type using null as a representation
        let dict = Dictionary<WrapType<'T>, obj>(wper)

        seq
        |> List.filter (fun v ->
            let key = Wrap v

            if (per.InEqualityRelation v) then
                if dict.ContainsKey key then
                    false
                else
                    (dict[key] <- null
                     true)
            else
                true)

//-------------------------------------------------------------------------
// Library: Name maps
//------------------------------------------------------------------------

type NameMap<'T> = Map<string, 'T>

type NameMultiMap<'T> = NameMap<'T list>

type MultiMap<'T, 'U when 'T: comparison> = Map<'T, 'U list>

module NameMap =

    let empty = Map.empty

    let range m =
        List.rev (Map.foldBack (fun _ x sofar -> x :: sofar) m [])

    let foldBack f (m: NameMap<'T>) z = Map.foldBack f m z

    let forall f m =
        Map.foldBack (fun x y sofar -> sofar && f x y) m true

    let exists f m =
        Map.foldBack (fun x y sofar -> sofar || f x y) m false

    let ofKeyedList f l =
        List.foldBack (fun x acc -> Map.add (f x) x acc) l Map.empty

    let ofList l : NameMap<'T> = Map.ofList l

    let ofSeq l : NameMap<'T> = Map.ofSeq l

    let toList (l: NameMap<'T>) = Map.toList l

    let layer (m1: NameMap<'T>) m2 = Map.foldBack Map.add m1 m2

    /// Not a very useful function - only called in one place - should be changed
    let layerAdditive addf m1 m2 =
        Map.foldBack (fun x y sofar -> Map.add x (addf (Map.tryFindMulti x sofar) y) sofar) m1 m2

    /// Union entries by identical key, using the provided function to union sets of values
    let union unionf (ms: NameMap<_> seq) =
        seq {
            for m in ms do
                yield! m
        }
        |> Seq.groupBy (fun (KeyValue (k, _v)) -> k)
        |> Seq.map (fun (k, es) -> (k, unionf (Seq.map (fun (KeyValue (_k, v)) -> v) es)))
        |> Map.ofSeq

    /// For every entry in m2 find an entry in m1 and fold
    let subfold2 errf f m1 m2 acc =
        Map.foldBack
            (fun n x2 acc ->
                try
                    f n (Map.find n m1) x2 acc
                with :? KeyNotFoundException ->
                    errf n x2)
            m2
            acc

    let suball2 errf p m1 m2 =
        subfold2 errf (fun _ x1 x2 acc -> p x1 x2 && acc) m1 m2 true

    let mapFold f s (l: NameMap<'T>) =
        Map.foldBack (fun x y (l2, sx) -> let y2, sy = f sx x y in Map.add x y2 l2, sy) l (Map.empty, s)

    let foldBackRange f (l: NameMap<'T>) acc =
        Map.foldBack (fun _ y acc -> f y acc) l acc

    let filterRange f (l: NameMap<'T>) =
        Map.foldBack (fun x y acc -> if f y then Map.add x y acc else acc) l Map.empty

    let mapFilter f (l: NameMap<'T>) =
        Map.foldBack
            (fun x y acc ->
                match f y with
                | None -> acc
                | Some y' -> Map.add x y' acc)
            l
            Map.empty

    let map f (l: NameMap<'T>) = Map.map (fun _ x -> f x) l

    let iter f (l: NameMap<'T>) = Map.iter (fun _k v -> f v) l

    let partition f (l: NameMap<'T>) =
        Map.filter (fun _ x -> f x) l, Map.filter (fun _ x -> not (f x)) l

    let mem v (m: NameMap<'T>) = Map.containsKey v m

    let find v (m: NameMap<'T>) = Map.find v m

    let tryFind v (m: NameMap<'T>) = Map.tryFind v m

    let add v x (m: NameMap<'T>) = Map.add v x m

    let isEmpty (m: NameMap<'T>) = (Map.isEmpty m)

    let existsInRange p m =
        Map.foldBack (fun _ y acc -> acc || p y) m false

    let tryFindInRange p m =
        Map.foldBack
            (fun _ y acc ->
                match acc with
                | None -> if p y then Some y else None
                | _ -> acc)
            m
            None

module NameMultiMap =

    let existsInRange f (m: NameMultiMap<'T>) =
        NameMap.exists (fun _ l -> List.exists f l) m

    let find v (m: NameMultiMap<'T>) =
        match m.TryGetValue v with
        | true, r -> r
        | _ -> []

    let add v x (m: NameMultiMap<'T>) = NameMap.add v (x :: find v m) m

    let range (m: NameMultiMap<'T>) =
        Map.foldBack (fun _ x sofar -> x @ sofar) m []

    let rangeReversingEachBucket (m: NameMultiMap<'T>) =
        Map.foldBack (fun _ x sofar -> List.rev x @ sofar) m []

    let chooseRange f (m: NameMultiMap<'T>) =
        Map.foldBack (fun _ x sofar -> List.choose f x @ sofar) m []

    let map f (m: NameMultiMap<'T>) = NameMap.map (List.map f) m

    let empty: NameMultiMap<'T> = Map.empty

    let initBy f xs : NameMultiMap<'T> =
        xs |> Seq.groupBy f |> Seq.map (fun (k, v) -> (k, List.ofSeq v)) |> Map.ofSeq

    let ofList (xs: (string * 'T) list) : NameMultiMap<'T> =
        xs
        |> Seq.groupBy fst
        |> Seq.map (fun (k, v) -> (k, List.ofSeq (Seq.map snd v)))
        |> Map.ofSeq

module MultiMap =

    let existsInRange f (m: MultiMap<_, _>) =
        Map.exists (fun _ l -> List.exists f l) m

    let find v (m: MultiMap<_, _>) =
        match m.TryGetValue v with
        | true, r -> r
        | _ -> []

    let add v x (m: MultiMap<_, _>) = Map.add v (x :: find v m) m

    let range (m: MultiMap<_, _>) =
        Map.foldBack (fun _ x sofar -> x @ sofar) m []

    let empty: MultiMap<_, _> = Map.empty

    let initBy f xs : MultiMap<_, _> =
        xs |> Seq.groupBy f |> Seq.map (fun (k, v) -> (k, List.ofSeq v)) |> Map.ofSeq

type LayeredMap<'Key, 'Value when 'Key: comparison> = Map<'Key, 'Value>

[<AutoOpen>]
module MapAutoOpens =
    type Map<'Key, 'Value when 'Key: comparison> with

        static member Empty: Map<'Key, 'Value> = Map.empty

#if FSHARPCORE_USE_PACKAGE
        member x.Values = [ for KeyValue (_, v) in x -> v ]
#endif

        member x.AddMany(kvs: _[]) =
            (x, kvs) ||> Array.fold (fun x (KeyValue (k, v)) -> x.Add(k, v))

        member x.AddOrModify(key, f: 'Value option -> 'Value) = x.Add(key, f (x.TryFind key))

/// Immutable map collection, with explicit flattening to a backing dictionary
[<Sealed>]
type LayeredMultiMap<'Key, 'Value when 'Key: equality and 'Key: comparison>(contents: LayeredMap<'Key, 'Value list>) =

    member x.Add(k, v) =
        LayeredMultiMap(contents.Add(k, v :: x[k]))

    member _.Item
        with get k =
            match contents.TryGetValue k with
            | true, l -> l
            | _ -> []

    member x.AddMany(kvs: _[]) =
        (x, kvs) ||> Array.fold (fun x (KeyValue (k, v)) -> x.Add(k, v))

    member _.TryFind k = contents.TryFind k

    member _.TryGetValue k = contents.TryGetValue k

    member _.Values = contents.Values |> List.concat

    static member Empty: LayeredMultiMap<'Key, 'Value> = LayeredMultiMap LayeredMap.Empty
