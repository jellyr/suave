﻿module Suave.Tests.Owin

open Fuchu

open System
open System.Collections.Generic
open System.Net
open System.Net.Http
open System.Net.Http.Headers
open System.Text

open Suave
open Suave.Types
open Suave.Http
open Suave.Http.Applicatives
open Suave.Http.Writers
open Suave.Owin

open Suave.Tests.TestUtilities
open Suave.Testing

let eq msg a b =
  Assert.Equal(msg, a, b)

let eqs msg bs aas =
  Assert.Equal(msg, bs |> List.ofSeq, aas |> List.ofSeq)

let throws msg matcher fn =
  try fn () with e when matcher e -> ()

[<Tests>]
let unit =
  let create (m : (string * string) list) =
    OwinApp.DeltaDictionary(m)

  let createOwin () =
    let request = { HttpRequest.empty with ``method`` = HttpMethod.PUT }
    new OwinApp.OwinDictionary({ HttpContext.empty with request = request })

  testList "infrastructure" [
    testList "DeltaDictionary" [
      testCase "construct & Delta" <| fun () ->
        let subject = create ["a", "a-1"]
        eq "has a" [|"a-1"|] subject.Delta.["a"]

      testCase "interaction set/remove" <| fun _ ->
        let subject = create ["a", "a-1"]
        eqs "has a" ["a-1"] subject.Delta.["a"]

        eqs "has only a"
           ([ "a" ] :> _ seq)
           ((subject.Delta :> IDictionary<_, _>).Keys :> _ seq)

        (subject :> IDictionary<_, _>).["b"] <- [| "b-1"; "b-2" |]
        eqs "has b" ["b-1"; "b-2"] subject.Delta.["b"]

        eq "can remove b once" true ((subject :> IDictionary<_, _>).Remove("b"))
        throws "key not found exception on b"
               (function :? KeyNotFoundException -> true | _ -> false)
               (fun _ -> subject.Delta.["b"] |> ignore)
        eq "cannot remove b twice" false ((subject :> IDictionary<_, _>).Remove("b"))

        (subject :> IDictionary<_, _>).["b"] <- [| "b-3" |]
        eqs "has b once more" ["b-3"] subject.Delta.["b"]

        (subject :> IDictionary<_, _>).["b"] <- [| "b-4" |]
        eqs "can change b after remove" ["b-4"] ((subject.Delta :> IDictionary<_, _>).["b"])
        eq "can remove b after change b after remove" true ((subject :> IDictionary<_, _>).Remove("b"))
        
        (subject :> IDictionary<_, _>).["c"] <- [| "c-1" |]
        eqs "has a, c"
            ["a"; "c"]
            ((subject.Delta :> IDictionary<_, _>).Keys :> _ seq)

        eq "can remove a once" true ((subject :> IDictionary<_, _>).Remove("a"))
        eq "cannot remove a twice" false ((subject :> IDictionary<_, _>).Remove("a"))

        eq "cannot remove what's never been there" false ((subject :> IDictionary<_, _>).Remove("x"))

        (subject :> IDictionary<_, _>).["a"] <- [| "a-1" |]
        eqs "has a once more" ["a-1"] subject.Delta.["a"]
        eqs "can retrieve header using StringComparer.OrdinalIgnoreCase"
            ["a-1"]
            subject.Delta.["A"]
      ]

    testList "OwinDictionary" [
      testCase "read/write HttpMethod" <| fun _ ->
        let subj : IDictionary<_, _> = upcast createOwin ()
        eq "method" "PUT" (subj.[OwinConstants.requestMethod] |> unbox)
        subj.[OwinConstants.requestMethod] <- "get"

      testCase "read/write custom" <| fun _ ->
        let subj : IDictionary<_, _> = upcast createOwin ()
        subj.["testing.MyKey"] <- "oh yeah"
        eq "read back" "oh yeah" (subj.["testing.MyKey"] |> unbox)

      testCase "uses StringComparer.OrdinalIgnoreCase" <| fun _ ->
        let subj : IDictionary<_, _> = upcast createOwin ()
        subj.["testing.MyKey"] <- "oh yeah"
        eq "read back" "oh yeah" (subj.["Testing.MyKey"] |> unbox)
      ]
    ]

[<Tests>]
let endToEnd =
  let runWithConfig = runWith defaultConfig

  let owinHelloWorld (env : OwinEnvironment) =
    let hello = "Hello, OWIN!"B

    env.[OwinConstants.responseStatusCode] <- box 201

    // set content type, new reference, invalid charset
    let responseHeaders : IDictionary<string, string[]> = unbox env.[OwinConstants.responseHeaders]
    responseHeaders.["Content-Type"] <- [| "application/json; charset=utf-1" |]

    // overwrite invalid 1, new reference, invalid charset
    let responseHeaders' : IDictionary<string, string[]> = unbox env.[OwinConstants.responseHeaders]
    responseHeaders'.["Content-Type"] <- [| "text/plain; charset=utf-2" |]

    // overwrite invalid 2, old reference, invalid charset
    responseHeaders.["Content-Type"] <- [| "application/json; charset=utf-3" |]

    // overwrite final, new reference
    let responseHeaders'' : IDictionary<string, string[]> = unbox env.[OwinConstants.responseHeaders]
    responseHeaders''.["Content-Type"] <- [| "text/plain; charset=utf-8" |]

    let responseStream : IO.Stream = unbox env.[OwinConstants.responseBody]
    responseStream.Write(hello, 0, hello.Length)
    async.Return ()

  let composedApp =
    path "/owin"
      >>= setHeader "X-Custom-Before" "Before OWIN"
      >>= OwinApp.ofApp owinHelloWorld
      >>= setHeader "X-Custom-After" "After OWIN"

  testList "e2e" [
    testCase "Hello, OWIN!" <| fun _ ->
      let asserts (result : HttpResponseMessage) =
        eq "Content-Type" "text/plain; charset=utf-8" (result.Content.Headers.ContentType.ToString())
        eq "Http Status Code" HttpStatusCode.Created result.StatusCode
        eq "Content Length" ("Hello, OWIN!"B.LongLength) (result.Content.Headers.ContentLength.Value)
        eq "Contents" "Hello, OWIN!" (result.Content.ReadAsStringAsync().Result)

        match result.Headers.TryGetValues("X-Custom-Before") with
        | true, actual ->
          eqs "Headers set before the OWIN app func, are sent"
              ["Before OWIN"]
              actual
        | false, _ -> Tests.failtest "X-Custom-Before is missing"

        match result.Headers.TryGetValues("X-Custom-After") with
        | true, actual ->
          eqs "Headers after before the OWIN app func, are sent"
              ["After OWIN"]
              actual
        | false, _ -> Tests.failtest "X-Custom-After is missing"

      runWithConfig composedApp |> reqResp Types.GET "/owin" "" None None DecompressionMethods.GZip id asserts

    testCase "Empty OWIN app should return 200 OK" <| fun _ ->
      let owinDefaults (env : OwinEnvironment) =
        async.Return ()

      let composedApp =
        path "/owin" >>= OwinApp.ofApp owinDefaults

      let asserts (result : HttpResponseMessage) =
        eq "Http Status Code" HttpStatusCode.OK result.StatusCode
        eq "Reason Phrase" "OK" result.ReasonPhrase

      runWithConfig composedApp |> reqResp Types.GET "/owin" "" None None DecompressionMethods.GZip id asserts

    testCase "Custom status code" <| fun _ ->
      let noContent (env : OwinEnvironment) =
        env.[OwinConstants.responseStatusCode] <- box 204
        //env.[OwinConstants.responseReasonPhrase] <- box "Nothing to see here"
        async.Return ()

      let composedApp =
        path "/owin" >>= OwinApp.ofApp noContent

      let asserts (result : HttpResponseMessage) =
        eq "Http Status Code" HttpStatusCode.NoContent result.StatusCode
        eq "Reason Phrase" "Nothing to see here" result.ReasonPhrase

      runWithConfig composedApp |> reqResp Types.GET "/owin" "" None None DecompressionMethods.GZip id asserts

    testCase "Task signals completion with status code" <| fun _ ->
      let noContent (env : OwinEnvironment) =
        env.[OwinConstants.responseStatusCode] <- box 204
        async.Return ()

      let composedApp =
        path "/owin" >>= OwinApp.ofApp noContent

      let asserts (result : HttpResponseMessage) =
        eq "Http Status Code" HttpStatusCode.NoContent result.StatusCode
        eq "Reason Phrase set by server" "No Content" result.ReasonPhrase

      runWithConfig composedApp |> reqResp Types.GET "/owin" "" None None DecompressionMethods.GZip id asserts

    testCase "Task signals completion with status code and headers" <| fun _ ->
      let noContent (env : OwinEnvironment) =
        env.[OwinConstants.responseStatusCode] <- box 204
        let responseHeaders : IDictionary<string, string[]> = unbox env.[OwinConstants.responseHeaders]
        responseHeaders.["Content-Type"] <- [| "text/plain; charset=utf-8" |]
        async.Return ()

      let composedApp =
        path "/owin" >>= OwinApp.ofApp noContent

      let asserts (result : HttpResponseMessage) =
        eq "Http Status Code" HttpStatusCode.NoContent result.StatusCode
        eq "Reason Phrase set by server" "No Content" result.ReasonPhrase
        eq "Content-Type" "text/plain; charset=utf-8" (result.Content.Headers.ContentType.ToString())

      runWithConfig composedApp |> reqResp Types.GET "/owin" "" None None DecompressionMethods.GZip id asserts
    
    testCase "OWIN middleware run before OWIN app" <| fun _ ->
      let noContent (env : OwinEnvironment) =
        env.[OwinConstants.responseStatusCode] <- box 204
        async.Return ()
      
      let basicAuthMidFunc = OwinMidFunc(fun next -> OwinAppFunc(fun env ->
          let requestHeaders : IDictionary<string, string[]> = unbox env.[OwinConstants.requestHeaders]
          let credentials = requestHeaders.["Authorization"].[0].Split([|' '|]).[1].Split([|':'|])
          match credentials with
          | [|"foo";"bar"|] -> next.Invoke(env)
          | _ ->
            env.[OwinConstants.responseStatusCode] <- box 401
            Threading.Tasks.Task.FromResult() :> Threading.Tasks.Task
        ))

      let composedApp =
        path "/owin"
          >>= OwinApp.ofMidFunc basicAuthMidFunc
          >>= OwinApp.ofApp noContent

      let asserts (result : HttpResponseMessage) =
        eq "Http Status Code" HttpStatusCode.NoContent result.StatusCode
        eq "Reason Phrase set by server" "No Content" result.ReasonPhrase

      let sendAuthHeader (req : HttpRequestMessage) =
        req.Headers.Authorization <- Net.Http.Headers.AuthenticationHeaderValue("Basic", Convert.ToBase64String("foo:bar"B))
        req

      runWithConfig composedApp |> reqResp Types.GET "/owin" "" None None DecompressionMethods.GZip sendAuthHeader asserts

    testCase "OWIN middleware run after OWIN app" <| fun _ ->
      let noContent (env : OwinEnvironment) =
        env.[OwinConstants.responseStatusCode] <- box 204
        async.Return ()
      
      let setCustomHeaderAfter = OwinMidFunc(fun next -> OwinAppFunc(fun env ->
          next.Invoke(env).ContinueWith(fun _ ->
            let responseHeaders : IDictionary<string, string[]> = unbox env.[OwinConstants.requestHeaders]
            responseHeaders.["X-Custom-After"] <- [|"After OWIN"|]
          )
        ))

      let composedApp =
        path "/owin"
          >>= OwinApp.ofApp noContent
          >>= OwinApp.ofMidFunc setCustomHeaderAfter

      let asserts (result : HttpResponseMessage) =
        eq "Http Status Code" HttpStatusCode.NoContent result.StatusCode
        eq "Reason Phrase set by server" "No Content" result.ReasonPhrase

        match result.Headers.TryGetValues("X-Custom-Before") with
        | true, actual ->
          eqs "Headers set after the OWIN app func, are sent"
              ["After OWIN"]
              actual
        | false, _ -> Tests.failtest "X-Custom-After is missing"

      runWithConfig composedApp |> reqResp Types.GET "/owin" "" None None DecompressionMethods.GZip id asserts

    testCase "OWIN middleware run around OWIN app, set before" <| fun _ ->
      let noContent (env : OwinEnvironment) =
        env.[OwinConstants.responseStatusCode] <- box 204
        async.Return ()
      
      let setCustomHeaders = OwinMidFunc(fun next -> OwinAppFunc(fun env ->
          let responseHeaders : IDictionary<string, string[]> = unbox env.[OwinConstants.requestHeaders]
          responseHeaders.["X-Custom-Before"] <- [|"Before OWIN"|]
          next.Invoke(env).ContinueWith(fun _ ->
            let responseHeaders : IDictionary<string, string[]> = unbox env.[OwinConstants.requestHeaders]
            responseHeaders.["X-Custom-After"] <- [|"After OWIN"|]
          )
        ))

      let composedApp =
        path "/owin"
          // NOTE: really need some sort of splitter, as found in Freya.
          >>= OwinApp.ofMidFunc setCustomHeaders
          >>= OwinApp.ofApp noContent
      
      let asserts (result : HttpResponseMessage) =
        eq "Http Status Code" HttpStatusCode.NoContent result.StatusCode
        eq "Reason Phrase set by server" "No Content" result.ReasonPhrase

        match result.Headers.TryGetValues("X-Custom-Before") with
        | true, actual ->
          eqs "Headers set before the OWIN app func, are sent"
              ["Before OWIN"]
              actual
        | false, _ -> Tests.failtest "X-Custom-Before is missing"

        match result.Headers.TryGetValues("X-Custom-Before") with
        | true, actual ->
          eqs "Headers set after the OWIN app func, are sent"
              ["After OWIN"]
              actual
        | false, _ -> Tests.failtest "X-Custom-After is missing"

      runWithConfig composedApp |> reqResp Types.GET "/owin" "" None None DecompressionMethods.GZip id asserts

    testCase "OWIN middleware run around OWIN app, set after" <| fun _ ->
      let noContent (env : OwinEnvironment) =
        env.[OwinConstants.responseStatusCode] <- box 204
        async.Return ()
      
      let setCustomHeaders = OwinMidFunc(fun next -> OwinAppFunc(fun env ->
          let responseHeaders : IDictionary<string, string[]> = unbox env.[OwinConstants.requestHeaders]
          responseHeaders.["X-Custom-Before"] <- [|"Before OWIN"|]
          next.Invoke(env).ContinueWith(fun _ ->
            let responseHeaders : IDictionary<string, string[]> = unbox env.[OwinConstants.requestHeaders]
            responseHeaders.["X-Custom-After"] <- [|"After OWIN"|]
          )
        ))

      let composedApp =
        path "/owin"
          >>= OwinApp.ofApp noContent
          // NOTE: really need some sort of splitter, as found in Freya.
          >>= OwinApp.ofMidFunc setCustomHeaders
      
      let asserts (result : HttpResponseMessage) =
        eq "Http Status Code" HttpStatusCode.NoContent result.StatusCode
        eq "Reason Phrase set by server" "No Content" result.ReasonPhrase

        match result.Headers.TryGetValues("X-Custom-Before") with
        | true, actual ->
          eqs "Headers set before the OWIN app func, are sent"
              ["Before OWIN"]
              actual
        | false, _ -> Tests.failtest "X-Custom-Before is missing"

        match result.Headers.TryGetValues("X-Custom-Before") with
        | true, actual ->
          eqs "Headers set after the OWIN app func, are sent"
              ["After OWIN"]
              actual
        | false, _ -> Tests.failtest "X-Custom-After is missing"

      runWithConfig composedApp |> reqResp Types.GET "/owin" "" None None DecompressionMethods.GZip id asserts
    ]
